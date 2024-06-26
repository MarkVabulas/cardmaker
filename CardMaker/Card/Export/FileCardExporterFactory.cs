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
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CardMaker.Data;
using CardMaker.Events.Managers;
using CardMaker.Forms;
using Support.IO;
using Support.UI;

namespace CardMaker.Card.Export
{
    public class FileCardExporterFactory
    {
        enum ExportOptionKey
        {
            Format,
            NameFormat,
            NameFormatOverride,
            Folder,
            StitchSkipIndex,
            CardIndices
        }

        public static readonly HashSet<ImageFormat> SupportedSystemDrawingImageFormat = new HashSet<ImageFormat>
        {
            ImageFormat.Bmp,
            ImageFormat.Emf,
            ImageFormat.Exif,
            ImageFormat.Gif,
            ImageFormat.Icon,
            ImageFormat.Jpeg,
            ImageFormat.Png,
            ImageFormat.Tiff,
            ImageFormat.Wmf
        };

        private static readonly Dictionary<string, ImageFormat> StringToImageFormatDictionary =
            SupportedSystemDrawingImageFormat.ToList().ToDictionary(
                i => i.ToString(), i => i);

        public enum CardMakerExportImageFormat
        {
            Bmp,
            Emf,
            Exif,
            Gif,
            Icon,
            Jpeg,
            Png,
            Tiff,
            Wmf,
#if !MONO_BUILD
            Webp,
#endif
        }

        public static readonly Dictionary<CardMakerExportImageFormat, ImageFormat> CardMakerImageExportFormatToImageFormatDictionary =
            Enum.GetValues(typeof(CardMakerExportImageFormat))
                .Cast<CardMakerExportImageFormat>()
                .ToList()
                .ToDictionary(e => e, e => StringToImageFormatDictionary.TryGetValue(e.ToString(), out var eFormat) ? eFormat : null);

        public static readonly Dictionary<string, CardMakerExportImageFormat>
            StringToCardMakerImageExportFormatDictionary = Enum.GetValues(typeof(CardMakerExportImageFormat))
                .Cast<CardMakerExportImageFormat>()
                .ToList().ToDictionary(e => e.ToString().ToLower(), e => e);

        public static string[] AllowedImageFormatNames = Enum.GetValues(typeof(CardMakerExportImageFormat))
            .Cast<CardMakerExportImageFormat>().Select(e => e.ToString()).ToArray();

        public static CardMakerExportImageFormat[] AllowedImageFormats = Enum.GetValues(typeof(CardMakerExportImageFormat))
            .Cast<CardMakerExportImageFormat>().ToArray();

        public static CardExportBase BuildFileCardExporter(bool bExportAllLayouts)
        {
            return bExportAllLayouts ? BuildProjectExporter() : BuildLayoutExporter();
        }

        private static CardExportBase BuildProjectExporter()
        {
            var zQuery = FormUtils.InitializeQueryPanelDialog(new QueryPanelDialog("Export to Images", 750, false));
            var sDefinition = ProjectManager.Instance.LoadedProject.exportNameFormat; // default to the project level definition
            var nDefaultFormatIndex = GetLastFormatIndex();

            zQuery.AddPullDownBox("Format", AllowedImageFormatNames, nDefaultFormatIndex, ExportOptionKey.Format);
            zQuery.AddCheckBox("Override Layout File Name Formats", false, ExportOptionKey.NameFormatOverride);
            zQuery.AddNumericBox("Stitch Skip Index", CardMakerSettings.ExportStitchSkipIndex, 0, 65535, 1, 0, ExportOptionKey.StitchSkipIndex);
            zQuery.AddTextBox("File Name Format (optional)", sDefinition ?? string.Empty, false, ExportOptionKey.NameFormat);
                // associated check box and the file format override text box
            zQuery.AddEnableControl(ExportOptionKey.NameFormatOverride, ExportOptionKey.NameFormat);
            zQuery.AddFolderBrowseBox("Output Folder", 
                Directory.Exists(ProjectManager.Instance.LoadedProject.lastExportPath) ? ProjectManager.Instance.LoadedProject.lastExportPath : ProjectManager.Instance.ProjectPath, 
                ExportOptionKey.Folder);
            zQuery.UpdateEnableStates();

            if (DialogResult.OK != zQuery.ShowDialog(CardMakerInstance.ApplicationForm))
            {
                return null;
            }
            var sFolder = zQuery.GetString(ExportOptionKey.Folder);
            SetupExportFolder(sFolder);

            if (!Directory.Exists(sFolder))
            {
                FormUtils.ShowErrorMessage("The folder specified does not exist!");
                return null;
            }

            ProjectManager.Instance.LoadedProject.lastExportPath = sFolder;
            var nStartLayoutIdx = 0;
            var nEndLayoutIdx = ProjectManager.Instance.LoadedProject.Layout.Length - 1;
            var bOverrideLayout = false;
            bOverrideLayout = zQuery.GetBool(ExportOptionKey.NameFormatOverride);

            CardMakerSettings.IniManager.SetValue(IniSettings.LastImageExportFormat, AllowedImageFormatNames[zQuery.GetIndex(ExportOptionKey.Format)]);
            CardMakerSettings.ExportStitchSkipIndex = (int)zQuery.GetDecimal(ExportOptionKey.StitchSkipIndex);

            return new FileCardExporter(nStartLayoutIdx, nEndLayoutIdx, sFolder, bOverrideLayout ? zQuery.GetString(ExportOptionKey.NameFormat) : null,
                CardMakerSettings.ExportStitchSkipIndex, AllowedImageFormats[zQuery.GetIndex(ExportOptionKey.Format)]);
        }

        private static CardExportBase BuildLayoutExporter()
        {
            var zQuery = FormUtils.InitializeQueryPanelDialog(new QueryPanelDialog("Export to Images", 750, false));

            var sDefinition = LayoutManager.Instance.ActiveLayout.exportNameFormat;
            var nDefaultFormatIndex = GetLastFormatIndex();

            zQuery.AddPullDownBox("Format", AllowedImageFormatNames, nDefaultFormatIndex, ExportOptionKey.Format);
            zQuery.AddTextBox("Export Indices", string.Empty, false, ExportOptionKey.CardIndices);
            zQuery.AddNumericBox("Stitch Skip Index", CardMakerSettings.ExportStitchSkipIndex, 0, 65535, 1, 0, ExportOptionKey.StitchSkipIndex);
            zQuery.AddTextBox("File Name Format (optional)", sDefinition ?? string.Empty, false, ExportOptionKey.NameFormat);
            zQuery.AddFolderBrowseBox("Output Folder", 
                Directory.Exists(ProjectManager.Instance.LoadedProject.lastExportPath) ? ProjectManager.Instance.LoadedProject.lastExportPath : ProjectManager.Instance.ProjectPath, 
                ExportOptionKey.Folder);

            zQuery.UpdateEnableStates();

            if (DialogResult.OK != zQuery.ShowDialog(CardMakerInstance.ApplicationForm))
            {
                return null;
            }
            var sFolder = zQuery.GetString(ExportOptionKey.Folder);
            SetupExportFolder(sFolder);

            if (!Directory.Exists(sFolder))
            {
                FormUtils.ShowErrorMessage("The folder specified does not exist!");
                return null;
            }

            ProjectManager.Instance.LoadedProject.lastExportPath = sFolder;
            var nLayoutIndex = ProjectManager.Instance.GetLayoutIndex(LayoutManager.Instance.ActiveLayout);
            if (-1 == nLayoutIndex)
            {
                FormUtils.ShowErrorMessage("Unable to determine the current layout. Please select a layout in the tree view and try again.");
                return null;
            }

            var zCardIndicesResult = ExportUtil.GetCardIndices(zQuery.GetString(ExportOptionKey.CardIndices));
            int[] arrayExportCardIndices = null;
            if (zCardIndicesResult != null)
            {
                if (!string.IsNullOrWhiteSpace(zCardIndicesResult.Item1))
                {
                    FormUtils.ShowErrorMessage("Unable to determine export indices: " + zCardIndicesResult.Item1);
                    return null;
                }
                arrayExportCardIndices = zCardIndicesResult.Item2;
            }


            CardMakerSettings.IniManager.SetValue(IniSettings.LastImageExportFormat, AllowedImageFormatNames[zQuery.GetIndex(ExportOptionKey.Format)]);
            CardMakerSettings.ExportStitchSkipIndex = (int)zQuery.GetDecimal(ExportOptionKey.StitchSkipIndex);

            return new FileCardExporter(nLayoutIndex, nLayoutIndex, sFolder,
                zQuery.GetString(ExportOptionKey.NameFormat),
                CardMakerSettings.ExportStitchSkipIndex, AllowedImageFormats[zQuery.GetIndex(ExportOptionKey.Format)])
            {
                ExportCardIndices = arrayExportCardIndices
            };
        }

        public static CardExportBase BuildImageExporter()
        {
            var zQuery = FormUtils.InitializeQueryPanelDialog(new QueryPanelDialog("Export Image", 750, false));

            var sDefinition = LayoutManager.Instance.ActiveLayout.exportNameFormat;
            var nDefaultFormatIndex = GetLastFormatIndex();

            zQuery.AddPullDownBox("Format", AllowedImageFormatNames, nDefaultFormatIndex, ExportOptionKey.Format);
            zQuery.AddTextBox("File Name Format (optional)", sDefinition ?? string.Empty, false, ExportOptionKey.NameFormat);
            zQuery.AddFolderBrowseBox("Output Folder",
                Directory.Exists(ProjectManager.Instance.LoadedProject.lastExportPath) ? ProjectManager.Instance.LoadedProject.lastExportPath : string.Empty,
                ExportOptionKey.Folder);

            zQuery.UpdateEnableStates();

            if (DialogResult.OK != zQuery.ShowDialog(CardMakerInstance.ApplicationForm))
            {
                return null;
            }
            var sFolder = zQuery.GetString(ExportOptionKey.Folder);
            SetupExportFolder(sFolder);

            if (!Directory.Exists(sFolder))
            {
                FormUtils.ShowErrorMessage("The folder specified does not exist!");
                return null;
            }

            ProjectManager.Instance.LoadedProject.lastExportPath = sFolder;
            var nLayoutIndex = ProjectManager.Instance.GetLayoutIndex(LayoutManager.Instance.ActiveLayout);
            if (-1 == nLayoutIndex)
            {
                FormUtils.ShowErrorMessage("Unable to determine the current layout. Please select a layout in the tree view and try again.");
                return null;
            }

            CardMakerSettings.IniManager.SetValue(IniSettings.LastImageExportFormat, AllowedImageFormatNames[zQuery.GetIndex(ExportOptionKey.Format)]);

            return new FileCardSingleExporter(nLayoutIndex, nLayoutIndex + 1, sFolder, zQuery.GetString(ExportOptionKey.NameFormat),
                AllowedImageFormats[zQuery.GetIndex(ExportOptionKey.Format)], LayoutManager.Instance.ActiveDeck.CardIndex);
        }

        public static CardExportBase BuildImageClipboardExporter()
        {
            var nLayoutIndex = ProjectManager.Instance.GetLayoutIndex(LayoutManager.Instance.ActiveLayout);
            if (-1 == nLayoutIndex)
            {
                FormUtils.ShowErrorMessage("Unable to determine the current layout. Please select a layout in the tree view and try again.");
                return null;
            }
            return new FileCardClipboardExporter(nLayoutIndex, nLayoutIndex + 1, LayoutManager.Instance.ActiveDeck.CardIndex);
        }

        private static int GetLastFormatIndex()
        {
            var lastImageFormat = CardMakerSettings.IniManager.GetValue(IniSettings.LastImageExportFormat, string.Empty);

            if (lastImageFormat != string.Empty)
            {
                for (var nIdx = 0; nIdx < AllowedImageFormatNames.Length; nIdx++)
                {
                    if (AllowedImageFormatNames[nIdx].Equals(lastImageFormat))
                    {
                        return nIdx;
                    }
                }
            }
            return 0;
        }

        private static void SetupExportFolder(string sFolder)
        {
            if (Directory.Exists(sFolder))
            {
                return;
            }
            try
            {
                Directory.CreateDirectory(sFolder);
            }
            catch (Exception e)
            {
                Logger.AddLogLine("Error creating folder {0}: {1}".FormatString(sFolder, e.Message));
            }
        }
    }
}
