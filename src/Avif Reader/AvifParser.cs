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

        public long? TryCalculateItemOffset(ItemLocationEntry entry)
        {
            if (entry is null)
            {
                ExceptionUtil.ThrowArgumentNullException(nameof(entry));
            }

            ulong offset;

            if (entry.ConstructionMethod == ConstructionMethod.FileOffset)
            {
                offset = entry.BaseOffset + entry.Extent.Offset;

                if ((offset + entry.Extent.Length) > this.fileLength)
                {
                    return null;
                }
            }
            else if (entry.ConstructionMethod == ConstructionMethod.IDatBoxOffset)
            {
                ItemDataBox dataBox = this.metaBox.ItemData;

                if (dataBox is null)
                {
                    return null;
                }

                if ((entry.Extent.Offset + entry.Extent.Length) > (ulong)dataBox.Length)
                {
                    return null;
                }

                offset = (ulong)dataBox.Offset + entry.Extent.Offset;

                if ((offset + entry.Extent.Length) > this.fileLength)
                {
                    return null;
                }
            }
            else
            {
                throw new FormatException($"ItemLocationEntry construction method { entry.ConstructionMethod } is not supported.");
            }

            if (offset > long.MaxValue)
            {
                return null;
            }

            return (long)offset;
        }

        public IItemProperty TryGetAssociatedItemProperty(uint itemId, FourCC propertyType)
        {
            IReadOnlyList<ItemPropertyAssociationEntry> items = this.metaBox.ItemProperties.TryGetAssociatedProperties(itemId);

            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    IItemProperty property = TryGetItemProperty(items[i].PropertyIndex);

                    if (property != null && property.Type == propertyType)
                    {
                        return property;
                    }
                }
            }

            return null;
        }

        public ColorInformationBox TryGetColorInfoBox(uint itemId)
        {
            IReadOnlyList<ItemPropertyAssociationEntry> items = this.metaBox.ItemProperties.TryGetAssociatedProperties(itemId);

            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    IItemProperty property = TryGetItemProperty(items[i].PropertyIndex);

                    if (property != null && property.Type == BoxTypes.ColorInformation)
                    {
                        return (ColorInformationBox)property;
                    }
                }
            }

            return null;
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
            IReadOnlyList<IItemInfoEntry> entries = this.metaBox.ItemInfo.Entries;

            for (int i = 0; i < entries.Count; i++)
            {
                IItemInfoEntry item = entries[i];

                if (item.ItemId == itemId)
                {
                    return item;
                }
            }

            return null;
        }

        public ItemLocationEntry TryGetItemLocation(uint itemId)
        {
            IReadOnlyList<ItemLocationEntry> entries = this.metaBox.ItemLocations.LocationEntries;

            for (int i = 0; i < entries.Count; i++)
            {
                ItemLocationEntry item = entries[i];

                if (item.ItemId == itemId)
                {
                    return item;
                }
            }

            return null;
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
            IReadOnlyList<ItemPropertyAssociationEntry> items = this.metaBox.ItemProperties.TryGetAssociatedProperties(itemId);

            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    IItemProperty property = TryGetItemProperty(items[i].PropertyIndex);

                    if (property != null && property.Type == BoxTypes.AuxiliaryTypeProperty)
                    {
                        AuxiliaryTypePropertyBox auxiliaryTypeBox = (AuxiliaryTypePropertyBox)property;

                        string auxType = auxiliaryTypeBox.AuxType.Value;

                        if (string.Equals(auxType, AlphaChannelNames.AVIF, StringComparison.Ordinal) ||
                            string.Equals(auxType, AlphaChannelNames.HEVC, StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
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

        private ImageGridDescriptor TryGetImageGridDescriptor(uint itemId)
        {
            IItemInfoEntry entry = TryGetItemInfoEntry(itemId);

            if (entry != null && entry.ItemType == ItemInfoEntryTypes.ImageGrid)
            {
                ItemLocationEntry locationEntry = TryGetItemLocation(itemId);

                if (locationEntry != null)
                {
                    if (locationEntry.Extent.Length < ImageGridDescriptor.SmallDescriptorLength)
                    {
                        ExceptionUtil.ThrowFormatException("Invalid image grid descriptor length.");
                    }

                    long? offset = TryCalculateItemOffset(locationEntry);
                    if (!offset.HasValue)
                    {
                        ExceptionUtil.ThrowFormatException("The image grid descriptor has an invalid file offset.");
                    }

                    this.reader.Position = offset.Value;
                    return new ImageGridDescriptor(this.reader, locationEntry.Extent.Length);
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
