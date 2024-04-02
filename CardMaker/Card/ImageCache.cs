﻿////////////////////////////////////////////////////////////////////////////////
// The MIT License (MIT)
//
// Copyright (c) 2024 Tim Stair
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using CardMaker.Data;
using CardMaker.Events.Managers;
using CardMaker.XML;
using PhotoshopFile;
#if !MONO_BUILD
using SkiaSharp;
using SkiaSharp.Views.Desktop;
#endif
using Support.IO;
using Support.UI;

namespace CardMaker.Card
{
    public static class ImageCache
    {
        private const int IMAGE_CACHE_MAX = 100;
        // cache of plain images (no adjustments)
        private static readonly Dictionary<string, Bitmap> s_dictionaryImages = new Dictionary<string, Bitmap>();
        // cache of images with in-memory tweaks
        private static readonly Dictionary<string, Bitmap> s_dictionaryCustomImages = new Dictionary<string, Bitmap>();

        public static void ClearImageCaches()
        {
            DumpImagesFromDictionary(s_dictionaryImages);
            DumpImagesFromDictionary(s_dictionaryCustomImages);
        }

        public static Bitmap LoadCustomImageFromCache(string sFile, ProjectLayoutElement zElement,
            int nTargetWidth = -1, int nTargetHeight = -1)
        {
            return LoadCustomImageFromCache(sFile, zElement, zElement.GetElementColor(), nTargetWidth, nTargetHeight, zElement.GetMirrorType());
        }

        public static Bitmap LoadCustomImageFromCache(string sFile, ProjectLayoutElement zElement, Color colorOverride, int nTargetWidth = -1, int nTargetHeight = -1, MirrorType eMirrorType = MirrorType.None)
        {
            var sKey = sFile.ToLower() + ":" + zElement.opacity + ":" + nTargetWidth + ":" + nTargetHeight + ProjectLayoutElement.GetElementColorString(colorOverride) + 
                       ":" + eMirrorType;

            if (s_dictionaryCustomImages.TryGetValue(sKey, out var zDestinationBitmap))
            {
                return zDestinationBitmap;
            }

            var zElementType = EnumUtil.GetElementType(zElement.type);

            var zSourceBitmap = LoadImageFromCache(sFile);
            if (null == zSourceBitmap)
            {
                return null;
            }
            // if the desired width/height/opacity match the 'plain' cached copy just return it (or special color handling for certain element types)
            // TODO: make a method for this just to shrink all this logic down
            if (
                (
                    (-1 == nTargetWidth || zSourceBitmap.Width == nTargetWidth)
                    && (-1 == nTargetHeight || zSourceBitmap.Height == nTargetHeight)
                    && 255 == zElement.opacity
                )
            )
            {
                switch (zElementType)
                {
                    case ElementType.FormattedText:
                    case ElementType.Graphic:
                        if (colorOverride == Color.Black)
                        {
                            return zSourceBitmap;
                        }
                        break;
                    default:
                        return zSourceBitmap;
                }
                
            }
            // TODO: should this be handled in a shared way?
            // TODO: this is a terrible eviction strategy
            if (s_dictionaryCustomImages.Count > IMAGE_CACHE_MAX)
            {
                DumpImagesFromDictionary(s_dictionaryCustomImages);
            }

            var zImageAttributes = new ImageAttributes();
            var zColor = new ColorMatrix();
            if (255 != zElement.opacity)
            {
                zColor.Matrix33 = (float) zElement.opacity / 255.0f;
            }
            // special color handling for certain element types
            if (colorOverride != Color.Black)
            {
                switch (zElementType)
                {
                    case ElementType.FormattedText:
                    case ElementType.Graphic:
                        zColor.Matrix40 = (float)colorOverride.R / 255.0f;
                        zColor.Matrix41 = (float)colorOverride.G / 255.0f;
                        zColor.Matrix42 = (float)colorOverride.B / 255.0f;
                        break;
                }
            }
            zImageAttributes.SetColorMatrix(zColor);

            nTargetWidth = nTargetWidth == -1 ? zSourceBitmap.Width : nTargetWidth;
            nTargetHeight = nTargetHeight == -1 ? zSourceBitmap.Height : nTargetHeight;

            zDestinationBitmap = new Bitmap(nTargetWidth, nTargetHeight); // target image
            var zGraphics = Graphics.FromImage(zDestinationBitmap);

            MirrorRender.MirrorElementGraphicTransform(zGraphics, zElement, eMirrorType, nTargetWidth, nTargetHeight);

            // draw the source image into the destination with the desired opacity
            zGraphics.DrawImage(zSourceBitmap, new Rectangle(0, 0, nTargetWidth, nTargetHeight), 0, 0, zSourceBitmap.Width, zSourceBitmap.Height, GraphicsUnit.Pixel,
                zImageAttributes);
            CacheImage(s_dictionaryCustomImages, sKey, zDestinationBitmap);

            return zDestinationBitmap;
        }

        private static Bitmap LoadImageFromCache(string sFile)
        {
            Bitmap zBitmap;
            var sKey = sFile.ToLower();
            if (!s_dictionaryImages.TryGetValue(sKey, out zBitmap))
            {
                if (s_dictionaryImages.Count > IMAGE_CACHE_MAX)
                {
                    // TODO: this is a terrible eviction strategy
                    DumpImagesFromDictionary(s_dictionaryImages);
                }
                if (!File.Exists(sFile))
                {
                    sFile = ProjectManager.Instance.ProjectPath + sFile;
                    if (!File.Exists(sFile))
                    {
                        return null;
                    }
                }

                Bitmap zSourceImage;
                try
                {
                    switch (Path.GetExtension(sFile).ToLower())
                    {
                        case ".psd":
                            {
                                var zFile = new PsdFile();
                                zFile.Load(sFile);
                                zSourceImage = ImageDecoder.DecodeImage(zFile);
                            }
                            break;
#if !MONO_BUILD
                        case ".webp":
                            using (var zStream = SKFileStream.OpenStream(sFile))
                            {
                                zSourceImage = SKBitmap.Decode(zStream).ToBitmap();
                            }
                            break;
#endif
                        default:
                            zSourceImage = new Bitmap(sFile);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.AddLogLine("Unable to load image: {0} - {1}".FormatString(sFile, ex.ToString()));
                    // return a purple bitmap to indicate an error
                    zBitmap = new Bitmap(1, 1);
                    Graphics.FromImage(zBitmap).FillRectangle(Brushes.Purple, 0, 0, zBitmap.Width, zBitmap.Height);
                    return zBitmap;
                }

                zBitmap = new Bitmap(zSourceImage.Width, zSourceImage.Height);

                // copy the contents into the image
                Graphics zGraphics = Graphics.FromImage(zBitmap);
                zGraphics.DrawImage(zSourceImage, new Rectangle(0, 0, zBitmap.Width, zBitmap.Height), 0, 0, zBitmap.Width, zBitmap.Height, GraphicsUnit.Pixel);

                // duping the image into a memory copy allows the file to change (not locked by the application)
                zSourceImage.Dispose();
                CacheImage(s_dictionaryImages, sKey, zBitmap);
            }
            return zBitmap;
        }

        private static void CacheImage(IDictionary<string, Bitmap> dictionaryImageCache, string sKey, Bitmap zBitmap)
        {
            // preserve the aspect ratio on the tag
            zBitmap.Tag = (float)zBitmap.Width / (float)zBitmap.Height;
            dictionaryImageCache.Add(sKey, zBitmap);
        }

        private static void DumpImagesFromDictionary(Dictionary<string, Bitmap> dictionaryImages)
        {
            foreach (var zBitmap in dictionaryImages.Values)
            {
                zBitmap.Dispose();
            }
            dictionaryImages.Clear();
        }
    }
}
