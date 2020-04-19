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

using System;

namespace AvifFileType.Exif
{
    internal readonly struct IFDEntry
        : IEquatable<IFDEntry>
    {
        public const int SizeOf = 12;

        public IFDEntry(EndianBinaryReader reader)
        {
            this.Tag = reader.ReadUInt16();
            this.Type = (TagDataType)reader.ReadUInt16();
            this.Count = reader.ReadUInt32();
            this.Offset = reader.ReadUInt32();
        }

        public IFDEntry(ushort tag, TagDataType type, uint count, uint offset)
        {
            this.Tag = tag;
            this.Type = type;
            this.Count = count;
            this.Offset = offset;
        }

        public ushort Tag { get; }

        public TagDataType Type { get; }

        public uint Count { get; }

        public uint Offset { get; }

        public override bool Equals(object obj)
        {
            if (obj is IFDEntry entry)
            {
                return Equals(entry);
            }

            return false;
        }

        public bool Equals(IFDEntry other)
        {
            return this.Tag == other.Tag &&
                   this.Type == other.Type &&
                   this.Count == other.Count &&
                   this.Offset == other.Offset;
        }

        public override int GetHashCode()
        {
            int hashCode = 1198491158;

            unchecked
            {
                hashCode = (hashCode * -1521134295) + this.Tag.GetHashCode();
                hashCode = (hashCode * -1521134295) + this.Count.GetHashCode();
                hashCode = (hashCode * -1521134295) + this.Type.GetHashCode();
                hashCode = (hashCode * -1521134295) + this.Offset.GetHashCode();
            }

            return hashCode;
        }

        public void Write(System.IO.BinaryWriter writer)
        {
            writer.Write(this.Tag);
            writer.Write((ushort)this.Type);
            writer.Write(this.Count);
            writer.Write(this.Offset);
        }

        public static bool operator ==(IFDEntry left, IFDEntry right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(IFDEntry left, IFDEntry right)
        {
            return !(left == right);
        }
    }
}
