﻿////////////////////////////////////////////////////////////////////////
//
// This file is part of pdn-avif, a FileType plugin for Paint.NET
// that loads and saves AVIF images.
//
// Copyright (c) 2020 Nicholas Hayes
//
// This file is licensed under the MIT License.
// See LICENSE.txt for complete licensing and attribution information.
//
////////////////////////////////////////////////////////////////////////

using AvifFileType.AvifContainer;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AvifFileType
{
    [DebuggerTypeProxy(typeof(AvifParserDebugView))]
    internal sealed class AvifParser
        : IDisposable
    {
        // 81920 is the largest multiple of 4096 that is under the large object heap limit (around 85,000 bytes).
        // It is used as the managed buffer size cutoff to avoid having to allocate both an unmanaged buffer and
        // a temporary managed buffer for as many images as possible.
        //
        // When reading data into the unmanaged buffer the EndianBinaryReader will use a temporary managed
        // buffer that is this size.
        private const ulong ManagedAvifItemDataMaxSize = 81920;

        private FileTypeBox fileTypeBox;
        private MetaBox metaBox;
        private EndianBinaryReader reader;
        private readonly ulong fileLength;

        public AvifParser(Stream stream, bool leaveOpen)
        {
            if (stream is null)
            {
                ExceptionUtil.ThrowArgumentNullException(nameof(stream));
            }

            this.reader = new EndianBinaryReader(stream, Endianess.Big, leaveOpen);
            Parse();
            this.fileLength = (ulong)stream.Length;
        }

        public void Dispose()
        {
            if (this.reader != null)
            {
                this.reader.Dispose();
                this.reader = null;
            }
        }

        public uint GetAlphaItemId(uint primaryItemId)
        {
            uint alphaImageItemId = 0;

            IItemReferenceEntry entry = GetMatchingReferences(primaryItemId, ReferenceTypes.AuxiliaryImage).FirstOrDefault();

            if (entry != null && IsAlphaChannelItem(entry.FromItemId))
            {
                alphaImageItemId = entry.FromItemId;
            }

            return alphaImageItemId;
        }

        public uint GetPrimaryItemId()
        {
            return this.metaBox.PrimaryItem?.ItemId ?? 1;
        }

        public void GetTransformationProperties(uint itemId,
                                                out CleanApertureBox cleanAperture,
                                                out ImageRotateBox imageRotate,
                                                out ImageMirrorBox imageMirror)
        {
            cleanAperture = null;
            imageRotate = null;
            imageMirror = null;

            IReadOnlyList<ItemPropertyAssociationEntry> items = this.metaBox.ItemProperties.TryGetAssociatedProperties(itemId);

            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    IItemProperty property = TryGetItemProperty(items[i].PropertyIndex);

                    if (property != null)
                    {
                        if (property.Type == BoxTypes.CleanAperture)
                        {
                            cleanAperture = (CleanApertureBox)property;
                        }
                        else if (property.Type == BoxTypes.ImageRotation)
                        {
                            imageRotate = (ImageRotateBox)property;
                        }
                        else if (property.Type == BoxTypes.ImageMirror)
                        {
                            imageMirror = (ImageMirrorBox)property;
                        }
                    }
                }
            }
        }

        public bool HasUnsupportedEssentialProperties(uint itemId)
        {
            IReadOnlyList<ItemPropertyAssociationEntry> items = this.metaBox.ItemProperties.TryGetAssociatedProperties(itemId);

            if (items != null)
            {
                IReadOnlyList<IItemProperty> properties = this.metaBox.ItemProperties.Properties;

                for (int i = 0; i < items.Count; i++)
                {
                    ItemPropertyAssociationEntry entry = items[i];

                    if (entry.Essential)
                    {
                        uint propertyIndex = entry.PropertyIndex;

                        if (propertyIndex > 0 && propertyIndex <= (uint)properties.Count)
                        {
                            int itemIndex = (int)(propertyIndex - 1);

                            IItemProperty property = properties[itemIndex];

                            if (property is null)
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        public AvifItemData ReadItemData(ItemLocationEntry entry)
        {
            if (entry is null)
            {
                ExceptionUtil.ThrowArgumentNullException(nameof(entry));
            }

            AvifItemData data;

            if (entry.Extents.Count == 1)
            {
                long offset = CalculateExtentOffset(entry.BaseOffset, entry.ConstructionMethod, entry.Extents[0]);

                this.reader.Position = offset;

                ulong totalItemSize = entry.TotalItemSize;

                if (totalItemSize <= ManagedAvifItemDataMaxSize)
                {
                    byte[] bytes = this.reader.ReadBytes((int)totalItemSize);

                    data = new ManagedAvifItemData(bytes);
                }
                else
                {
                    UnmanagedAvifItemData unmanagedItemData = new UnmanagedAvifItemData(totalItemSize);

                    try
                    {
                        this.reader.ProperRead(unmanagedItemData.UnmanagedBuffer, 0, totalItemSize);

                        data = unmanagedItemData;
                        unmanagedItemData = null;
                    }
                    finally
                    {
                        unmanagedItemData?.Dispose();
                    }
                }
            }
            else
            {
                data = ReadDataFromMultipleExtents(entry);
            }

            return data;
        }

        public TProperty TryGetAssociatedItemProperty<TProperty>(uint itemId) where TProperty : class, IItemProperty
        {
            if (typeof(TProperty).IsAbstract)
            {
                ExceptionUtil.ThrowInvalidOperationException($"Cannot call this method with an abstract type, type: { typeof(TProperty).Name }.");
            }

            IReadOnlyList<ItemPropertyAssociationEntry> items = this.metaBox.ItemProperties.TryGetAssociatedProperties(itemId);

            if (items != null)
            {
                FourCC propertyType = ItemPropertyTypeMap.GetPropertyType<TProperty>();

                for (int i = 0; i < items.Count; i++)
                {
                    IItemProperty property = TryGetItemProperty(items[i].PropertyIndex);

                    if (property != null && property.Type == propertyType)
                    {
                        return (TProperty)property;
                    }
                }
            }

            return null;
        }

        public ColorInformationBox TryGetColorInfoBox(uint itemId)
        {
            return TryGetAssociatedItemProperty<ColorInformationBox>(itemId);
        }

        public ItemLocationEntry TryGetExifLocation(uint itemId)
        {
            foreach (IItemReferenceEntry item in GetMatchingReferences(itemId, ReferenceTypes.ContentDescription))
            {
                IItemInfoEntry entry = TryGetItemInfoEntry(item.FromItemId);

                if (entry != null && entry.ItemType == ItemInfoEntryTypes.Exif)
                {
                    return TryGetItemLocation(item.FromItemId);
                }
            }

            return null;
        }

        public ImageGridInfo TryGetImageGridInfo(uint itemId)
        {
            ImageGridDescriptor gridDescriptor = TryGetImageGridDescriptor(itemId);

            if (gridDescriptor != null)
            {
                IItemReferenceEntry derivedImageProperty = GetMatchingReferences(itemId, ReferenceTypes.DerivedImage).First();

                return new ImageGridInfo(derivedImageProperty.ToItemIds, gridDescriptor);
            }

            return null;
        }

        public IItemInfoEntry TryGetItemInfoEntry(uint itemId)
        {
            return this.metaBox.ItemInfo.TryGetEntry(itemId);
        }

        public ItemLocationEntry TryGetItemLocation(uint itemId)
        {
            return this.metaBox.ItemLocations.TryFindItem(itemId);
        }

        public ItemLocationEntry TryGetXmpLocation(uint itemId)
        {
            foreach (IItemReferenceEntry item in GetMatchingReferences(itemId, ReferenceTypes.ContentDescription))
            {
                IItemInfoEntry entry = TryGetItemInfoEntry(item.FromItemId);

                if (entry != null && entry.ItemType == ItemInfoEntryTypes.Mime)
                {
                    MimeItemInfoEntryBox mimeItemInfo = (MimeItemInfoEntryBox)entry;
                    string contentType = mimeItemInfo.ContentType.Value;

                    if (string.Equals(contentType, XmpItemInfoEntry.XmpContentType, StringComparison.Ordinal))
                    {
                        return TryGetItemLocation(item.FromItemId);
                    }
                }
            }

            return null;
        }

        private long CalculateExtentOffset(ulong baseOffset, ConstructionMethod constructionMethod, ItemLocationExtent extent)
        {
            if (extent is null)
            {
                ExceptionUtil.ThrowArgumentNullException(nameof(extent));
            }

            ulong offset;

            try
            {
                if (constructionMethod == ConstructionMethod.FileOffset)
                {
                    checked
                    {
                        offset = baseOffset + extent.Offset;

                        if ((offset + extent.Length) > this.fileLength)
                        {
                            throw new FormatException("The item has an invalid file offset.");
                        }
                    }
                }
                else if (constructionMethod == ConstructionMethod.IDatBoxOffset)
                {
                    ItemDataBox dataBox = this.metaBox.ItemData;

                    if (dataBox is null)
                    {
                        throw new FormatException("The file does not have an item data box.");
                    }

                    checked
                    {
                        if ((extent.Offset + extent.Length) > (ulong)dataBox.Length)
                        {
                            throw new FormatException("The item has an invalid data box offset.");
                        }

                        offset = (ulong)dataBox.Offset + extent.Offset;

                        if ((offset + extent.Length) > this.fileLength)
                        {
                            throw new FormatException("The item has an invalid file offset.");
                        }
                    }
                }
                else
                {
                    throw new FormatException($"ItemLocationEntry construction method { constructionMethod } is not supported.");
                }
            }
            catch (OverflowException ex)
            {
                throw new FormatException("Overflow when attempting to calculate the item file offset.", ex);
            }

            if (offset > long.MaxValue)
            {
                throw new FormatException($"The item file offset exceeds {long.MaxValue:F0} bytes.");
            }

            return (long)offset;
        }

        private void CheckForRequiredBoxes()
        {
            if (this.fileTypeBox is null)
            {
                ExceptionUtil.ThrowFormatException("The file does not contain a FileType box.");
            }

            if (this.metaBox is null)
            {
                ExceptionUtil.ThrowFormatException("The file does not contain a Meta box.");
            }

            if (this.metaBox.ItemInfo is null)
            {
                ExceptionUtil.ThrowFormatException("The file does not have an ItemInfo box.");
            }

            if (this.metaBox.ItemLocations is null)
            {
                ExceptionUtil.ThrowFormatException("The file does not have an ItemLocations box.");
            }

            if (this.metaBox.ItemProperties is null)
            {
                ExceptionUtil.ThrowFormatException("The file does not have an ItemProperties box.");
            }
        }

        private IEnumerable<IItemReferenceEntry> GetMatchingReferences(uint itemId, FourCC requiredReferenceType)
        {
            ItemReferenceBox itemReferenceBox = this.metaBox.ItemReferences;

            if (itemReferenceBox != null)
            {
                foreach (IItemReferenceEntry item in itemReferenceBox.ItemReferences)
                {
                    if (item.Type != requiredReferenceType)
                    {
                        continue;
                    }

                    if (item.Type == ReferenceTypes.DerivedImage)
                    {
                        // Derived images place the parent item id in the FromItemId field.
                        if (item.FromItemId == itemId)
                        {
                            yield return item;
                        }
                    }
                    else
                    {
                        IReadOnlyList<uint> toItemIds = item.ToItemIds;
                        for (int i = 0; i < toItemIds.Count; i++)
                        {
                            if (toItemIds[i] == itemId)
                            {
                                yield return item;
                            }
                        }
                    }

                }
            }
        }

        private bool IsAlphaChannelItem(uint itemId)
        {
            AuxiliaryTypePropertyBox auxiliaryTypeBox = TryGetAssociatedItemProperty<AuxiliaryTypePropertyBox>(itemId);

            if (auxiliaryTypeBox != null)
            {
                string auxType = auxiliaryTypeBox.AuxType.Value;

                if (string.Equals(auxType, AlphaChannelNames.AVIF, StringComparison.Ordinal) ||
                    string.Equals(auxType, AlphaChannelNames.HEVC, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private void Parse()
        {
            while (this.reader.Position < this.reader.Length)
            {
                Box header = new Box(this.reader);

                if (header.Type == BoxTypes.FileType)
                {
                    if (this.fileTypeBox != null)
                    {
                        ExceptionUtil.ThrowFormatException("The file contains multiple FileType boxes.");
                    }

                    EndianBinaryReaderSegment segment = this.reader.CreateSegment(header.DataStartOffset, header.DataLength);

                    this.fileTypeBox = new FileTypeBox(segment, header);
                    this.fileTypeBox.CheckForAvifCompatibility();
                }
                else if (header.Type == BoxTypes.Meta)
                {
                    if (this.metaBox != null)
                    {
                        ExceptionUtil.ThrowFormatException("The file contains multiple Meta boxes.");
                    }

                    EndianBinaryReaderSegment segment = this.reader.CreateSegment(header.DataStartOffset, header.DataLength);

                    this.metaBox = new MetaBox(segment, header);
                }
                else
                {
                    // Skip any other boxes
                    this.reader.Position = header.End;
                }
            }

            CheckForRequiredBoxes();
        }

        private AvifItemData ReadDataFromMultipleExtents(ItemLocationEntry entry)
        {
            AvifItemData data;

            IReadOnlyList<ItemLocationExtent> extents = entry.Extents;
            ulong totalItemSize = entry.TotalItemSize;

            if (totalItemSize <= ManagedAvifItemDataMaxSize)
            {
                byte[] bytes = new byte[(int)totalItemSize];

                int offset = 0;
                int remainingBytes = bytes.Length;

                for (int i = 0; i < extents.Count; i++)
                {
                    ItemLocationExtent extent = extents[i];

                    long itemOffset = CalculateExtentOffset(entry.BaseOffset, entry.ConstructionMethod, extent);

                    int length = (int)extent.Length;

                    if (length > remainingBytes)
                    {
                        throw new FormatException("The extent length is greater than the number of bytes remaining for the item.");
                    }

                    this.reader.Position = itemOffset;
                    this.reader.ProperRead(bytes, offset, length);

                    offset += length;
                    remainingBytes -= length;
                }

                if (remainingBytes > 0)
                {
                    // This should never happen, the total item size is the sum of all the extent sizes.
                    throw new FormatException("The item has more data than was read from the extents.");
                }

                data = new ManagedAvifItemData(bytes);
            }
            else
            {
                UnmanagedAvifItemData unmanagedItemData = new UnmanagedAvifItemData(totalItemSize);

                try
                {
                    ulong offset = 0;
                    ulong remainingBytes = totalItemSize;

                    for (int i = 0; i < extents.Count; i++)
                    {
                        ItemLocationExtent extent = extents[i];

                        long itemOffset = CalculateExtentOffset(entry.BaseOffset, entry.ConstructionMethod, extent);

                        ulong length = extent.Length;

                        if (length > remainingBytes)
                        {
                            throw new FormatException("The extent length is greater than the number of bytes remaining for the item.");
                        }

                        this.reader.Position = itemOffset;
                        this.reader.ProperRead(unmanagedItemData.UnmanagedBuffer, offset, length);

                        offset += length;
                        remainingBytes -= length;
                    }

                    if (remainingBytes > 0)
                    {
                        // This should never happen, the total item size is the sum of all the extent sizes.
                        throw new FormatException("The item has more data than was read from the extents.");
                    }

                    data = unmanagedItemData;
                    unmanagedItemData = null;
                }
                finally
                {
                    unmanagedItemData?.Dispose();
                }
            }

            return data;
        }

        private ImageGridDescriptor TryGetImageGridDescriptor(uint itemId)
        {
            IItemInfoEntry entry = TryGetItemInfoEntry(itemId);

            if (entry != null && entry.ItemType == ItemInfoEntryTypes.ImageGrid)
            {
                ItemLocationEntry locationEntry = TryGetItemLocation(itemId);

                if (locationEntry != null)
                {
                    if (locationEntry.TotalItemSize < ImageGridDescriptor.SmallDescriptorLength)
                    {
                        ExceptionUtil.ThrowFormatException("Invalid image grid descriptor length.");
                    }

                    using (AvifItemData itemData = ReadItemData(locationEntry))
                    {
                        Stream stream = null;

                        try
                        {
                            stream = itemData.GetStream();

                            using (EndianBinaryReader imageGridReader = new EndianBinaryReader(stream, this.reader.Endianess))
                            {
                                stream = null;

                                return new ImageGridDescriptor(imageGridReader, itemData.Length);
                            }
                        }
                        finally
                        {
                            stream?.Dispose();
                        }
                    }
                }
            }

            return null;
        }

        private IItemProperty TryGetItemProperty(uint propertyIndex)
        {
            IReadOnlyList<IItemProperty> properties = this.metaBox.ItemProperties.Properties;

            if (propertyIndex == 0 || propertyIndex > (uint)properties.Count)
            {
                return null;
            }

            return properties[(int)(propertyIndex - 1)];
        }

        private sealed class AvifParserDebugView
        {
            private readonly AvifParser parser;

            public AvifParserDebugView(AvifParser parser)
            {
                this.parser = parser;
            }

            public FileTypeBox FileTypeBox => this.parser.fileTypeBox;

            public MetaBox MetaBox => this.parser.metaBox;
        }
    }
}
