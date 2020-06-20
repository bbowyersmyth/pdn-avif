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

namespace AvifFileType.AvifContainer
{
    internal sealed class AV1ConfigBox
        : ItemProperty
    {
        // -- Byte 1
        // marker - 1 bit
        // version - 7 bits
        // -- Byte 2
        // seqProfile - 4 bits
        // seqLevelIdx0 - 4 bits
        // -- Byte 3
        // seqTier0 - 1 bit
        // highBitDepth - 1 bit
        // twelveBit - 1 bit
        // monochrome - 1 bit
        // chromaSubsamplingX - 1 bit
        // chromaSubsamplingY - 1 bit
        // chromaSmaplePosition - 3 bits
        // -- Byte 4
        //
        // The following AV1 configuration box values are not relevant when reading and
        // writing a still image, but are included for documentation purposes.
        //
        // reserved - 3 bits
        // initialPresentationDelayPresent - 1 bit
        // if (initialPresentationDelayPresent == 1) {
        //   initialPresentationDelayMinusOne - 4 bits
        // } else {
        //   reserved2 - 4 bits
        // }

        private const byte AV1CMarker = 1 << 7;
        private const byte AV1CVersion = 1;

        private const byte AV1CMarkerAndVersion = AV1CMarker | AV1CVersion;

        public AV1ConfigBox(EndianBinaryReader reader, Box header)
            : base(header)
        {
            byte markerAndVersion = reader.ReadByte();

            if (markerAndVersion != AV1CMarkerAndVersion)
            {
                throw new FormatException("Invalid AV1C box marker.");
            }

            byte seqProfileAndSeqLevelIdx0 = reader.ReadByte();
            byte configurationParameters = reader.ReadByte();

            this.SeqProfile = (byte)((seqProfileAndSeqLevelIdx0 >> 5) & 0x07);
            this.SeqLevelIdx0 = (byte)(seqProfileAndSeqLevelIdx0 & 0x1f);
            this.SeqTier0 = GetConfigurationOption(configurationParameters, 8);
            this.HighBitDepth = GetConfigurationOption(configurationParameters, 7);
            this.TwelveBit = GetConfigurationOption(configurationParameters, 6);
            this.Monochrome = GetConfigurationOption(configurationParameters, 5);
            this.ChromaSubsamplingX = GetConfigurationOption(configurationParameters, 4);
            this.ChromaSubsamplingY = GetConfigurationOption(configurationParameters, 3);
            this.ChromaSamplePosition = (ChromaSamplePosition)(configurationParameters & 0x03);
        }

        public AV1ConfigBox()
            : base(BoxTypes.AV1Config)
        {
        }

        public byte SeqProfile { get; set; }

        public byte SeqLevelIdx0 { get; set; }

        public bool SeqTier0 { get; set; }

        public bool HighBitDepth { get; set; }

        public bool TwelveBit { get; set; }

        public bool Monochrome { get; set; }

        public bool ChromaSubsamplingX { get; set; }

        public bool ChromaSubsamplingY { get; set; }

        public ChromaSamplePosition ChromaSamplePosition { get; set; }

        public override void Write(BigEndianBinaryWriter writer)
        {
            base.Write(writer);

            byte seqProfileAndSeqLevelIdx0 = (byte)((this.SeqProfile << 5) | (this.SeqLevelIdx0 & 0x1f));

            byte configurationParameters = (byte)(SetConfigurationOption(8, this.SeqTier0)
                                                | SetConfigurationOption(7, this.HighBitDepth)
                                                | SetConfigurationOption(6, this.TwelveBit)
                                                | SetConfigurationOption(5, this.Monochrome)
                                                | SetConfigurationOption(4, this.ChromaSubsamplingX)
                                                | SetConfigurationOption(3, this.ChromaSubsamplingY)
                                                | ((int)this.ChromaSamplePosition & 0x03));

            // The presentation parameters defined in byte 4 are not
            // relevant when reading or writing a still image.
            const byte presentationParameters = 0;

            writer.Write(AV1CMarkerAndVersion);
            writer.Write(seqProfileAndSeqLevelIdx0);
            writer.Write(configurationParameters);
            writer.Write(presentationParameters);
        }

        protected override ulong GetTotalBoxSize()
        {
            return base.GetTotalBoxSize()
                   + sizeof(byte)  // Marker and version
                   + sizeof(byte)  // SeqProfile and SeqLevelIdx0
                   + sizeof(byte)  // Configuration parameters
                   + sizeof(byte); // Presentation parameters
        }

        private bool GetConfigurationOption(byte value, int index)
        {
            return (value & (1 << (index - 1))) != 0;
        }

        private static int SetConfigurationOption(int index, bool value)
        {
            return value ? (1 << (index - 1)) : 0;
        }
    }
}
