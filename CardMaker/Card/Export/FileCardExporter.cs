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
using System.ComponentModel.Composition.Primitives;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using CardMaker.Data;
using CardMaker.Events.Managers;
using Support.IO;

namespace CardMaker.Card.Export
{
    public class FileCardExporter : CardExportBase
    {
        private readonly string m_sExportFolder;
        private readonly string m_sOverrideStringFormat;
        private readonly FileCardExporterFactory.CardMakerExportImageFormat m_eImageFormat;
        private readonly int m_nSkipStitchIndex;
        public int[] ExportCardIndices { get; set; }

        public FileCardExporter(int nLayoutStartIndex, int nLayoutEndIdx, string sExportFolder, string sOverrideStringFormat, int nSkipStitchIndex, FileCardExporterFactory.CardMakerExportImageFormat eImageFormat) 
            : this(Enumerable.Range(nLayoutStartIndex, (nLayoutEndIdx - nLayoutStartIndex) + 1).ToArray(), sExportFolder, sOverrideStringFormat, nSkipStitchIndex, eImageFormat)
        {

        }

        public FileCardExporter(int[] arrayExportLayoutIndices, string sExportFolder, string sOverrideStringFormat, int
            nSkipStitchIndex, FileCardExporterFactory.CardMakerExportImageFormat eImageFormat)
            : base(arrayExportLayoutIndices)
        {
            if (!sExportFolder.EndsWith(Path.DirectorySeparatorChar.ToString(CultureInfo.InvariantCulture)))
            {
                sExportFolder += Path.DirectorySeparatorChar;
            }

            m_sExportFolder = sExportFolder;
            m_sOverrideStringFormat = sOverrideStringFormat;

            m_nSkipStitchIndex = nSkipStitchIndex;
            m_eImageFormat = eImageFormat;
        }

        private List<int> GetSubLayoutArray(int PrimaryLayoutIdx)
        {
			List<int> SubLayouts = new List<int>();

            // Loop through the valid elements in this current layout to get a list of other layouts we need to generate first
            for (var nIdx = 0; nIdx < CurrentDeck.CardLayout.Element.Length; nIdx++)
			{
				var zElement = CurrentDeck.CardLayout.Element[nIdx];
                if (!zElement.enabled)
					continue;

                // Check for the correct element type so we know it's a SubLayout request
                if (zElement.type == ElementType.SubLayout.ToString())
                {
                    // Get the translated string from the current deck
                    ElementString zElementString = CurrentDeck.TranslateString(zElement.variable, CurrentDeck.CurrentPrintLine, zElement, true);

                    // Get the index of the referenced layout
					var nLayoutIdx = ProjectManager.Instance.GetLayoutIndex(zElementString.String);

					// Append the requested layout to the list of SubLayouts
					if (nLayoutIdx >= 0)
					    SubLayouts.Add(nLayoutIdx);
                }
			}

            return SubLayouts;
        }

        public override void ExportThread()
        {
            var progressLayoutIdx = (m_bDoReporting) ? ProgressReporter.GetProgressIndex(ProgressName.LAYOUT) : 0;
            var progressCardIdx = (m_bDoReporting) ? ProgressReporter.GetProgressIndex(ProgressName.CARD) : 0;

            // Exports may put multiple cards into a single exported image (referred to as a container below)

            if (m_bDoReporting) ProgressReporter.ProgressReset(progressLayoutIdx, 0, ExportLayoutIndices.Length, 0);
            foreach (var nIdx in ExportLayoutIndices)
			{
				ChangeExportLayoutIndex(nIdx);
                if (CurrentDeck.EmptyReference)
                {
					// empty reference layouts are not exported
					if (m_bDoReporting) ProgressReporter.ProgressStep(progressLayoutIdx);
                    continue;
				}
				var nCardCountPadSize = CurrentDeck.CardCount.ToString(CultureInfo.InvariantCulture).Length;
				if (m_bDoReporting) ProgressReporter.ProgressReset(progressCardIdx, 0, CurrentDeck.CardCount, 0);

                var exportContainerWidth = CurrentDeck.CardLayout.exportWidth == 0
                    ? CurrentDeck.CardLayout.width : CurrentDeck.CardLayout.exportWidth;

                var exportContainerHeight = CurrentDeck.CardLayout.exportHeight == 0
                    ? CurrentDeck.CardLayout.height : CurrentDeck.CardLayout.exportHeight;

                if (CurrentDeck.CardLayout.width > exportContainerWidth ||
                    CurrentDeck.CardLayout.height > exportContainerHeight)
                {
                    Logger.AddLogLine(
                        $"ERROR: Layout: [{CurrentDeck.CardLayout.Name}] exportWidth and/or exportHeight too small! (Skipping export)");
                    continue;
                }

                // swap width/height if necessary for rotation
                var currentCardWidth = CurrentDeck.CardLayout.width;
                var currentCardHeight = CurrentDeck.CardLayout.height;
                if (Math.Abs(CurrentDeck.CardLayout.exportRotation) == 90)
                {
                    (exportContainerWidth, exportContainerHeight) = (exportContainerHeight, exportContainerWidth);
                    (currentCardWidth, currentCardHeight) = (currentCardHeight, currentCardWidth);
                }

				List<int> vSubLayoutArray = GetSubLayoutArray(nIdx);

				UpdateBufferBitmap(exportContainerWidth, exportContainerHeight);
                // The graphics must be initialized BEFORE the resolution of the bitmap is set (graphics will be the same DPI as the application/card)
                var zContainerGraphics = Graphics.FromImage(m_zExportCardBuffer);
                var arrayCardIndices = GetCardIndicesArray(CurrentDeck);
                for (var nCardArrayIdx = 0; nCardArrayIdx < arrayCardIndices.Length; nCardArrayIdx++)
				{
					int nCardId;
                    var nX = 0;
                    var nY = 0;
                    var nCardsExportedInImage = 0;
                    ClearGraphics(zContainerGraphics);
                    do
                    {
                        // NOTE: If this loops to create a multi-card image the cardId needs to be updated
                        nCardId = arrayCardIndices[nCardArrayIdx];
                        CurrentDeck.ResetDeckCache();
                        // ANNOYING - the printcard index is 0 based but all other uses of nCardId are 1 based
                        CurrentDeck.CardPrintIndex = nCardId;

                        // This loops through the  Sub Layouts and sends them on
						for (var nSubIdx = 0; nSubIdx < vSubLayoutArray.Count(); nSubIdx++)
						{
							var nSubLayoutIdx = vSubLayoutArray[nSubIdx];
							var SubLayoutExporter = new FileCardExporter(nSubLayoutIdx, nSubLayoutIdx, m_sExportFolder, null, -1, m_eImageFormat);
							SubLayoutExporter.CurrentDeck.ParentDictionaryColumnNameToIndex = CurrentDeck.Translator.DictionaryColumnNameToIndex;
							SubLayoutExporter.CurrentDeck.ParentPrintLine = CurrentDeck.CurrentPrintLine;
							SubLayoutExporter.m_bDoReporting = false;
							SubLayoutExporter.ExportThread();
						}

                        // This is an extra call to ChangeExportLayoutIndex since it switches what the active layout is during the previous ExecuteThread() for the SubLayout
                        if (vSubLayoutArray.Count() > 0)
                        {
                            ChangeExportLayoutIndex(nIdx);

                            // We also need to reset everything associated with our own deck since it gets messed up in the SubLayout
                            CurrentDeck.ResetDeckCache();
                            
                            // Need to update the CardPrintIndex back to the correct one for this Layout, since it was previously changed in the SubLayout
                            CurrentDeck.CardPrintIndex = nCardId;
                        }

						nCardsExportedInImage++;
#warning TODO: optimize this by only creating the bitmap when necessary                        
                        var bitmapSingleCard = new Bitmap(CurrentDeck.CardLayout.width, CurrentDeck.CardLayout.height);
                        var zSingleCardGraphics = Graphics.FromImage(bitmapSingleCard);

                        ClearGraphics(zSingleCardGraphics);
                        CardRenderer.DrawPrintLineToGraphics(zSingleCardGraphics, 0, 0, !CurrentDeck.CardLayout.exportTransparentBackground);
                        ProcessRotateExport(bitmapSingleCard, CurrentDeck.CardLayout, false);
                        zContainerGraphics.DrawImage(bitmapSingleCard, nX, nY);

						if (m_bDoReporting) ProgressReporter.ProgressStep(progressCardIdx);

                        var nMoveCount = 1;
                        if (m_nSkipStitchIndex > 0)
                        {
                            var x = ((nCardsExportedInImage + 1)%m_nSkipStitchIndex);
                            if (x == 0)
                            {
                                // shift forward an extra spot to ignore the dummy index
                                nMoveCount = 2;
                            }
                        }

                        var bOutOfSpace = false;

                        for (var nShift = 0; nShift < nMoveCount; nShift++)
                        {
#warning this is messed up for rotated layouts
                            nX += currentCardWidth + CurrentDeck.CardLayout.buffer;
                            if (nX + currentCardWidth > exportContainerWidth)
                            {
                                nX = 0;
                                nY += currentCardHeight + CurrentDeck.CardLayout.buffer;
                            }
                            if (nY + currentCardHeight > exportContainerHeight)
                            {
                                // no more space
                                bOutOfSpace = true;
                                break;
                            }
                        }

                        if (bOutOfSpace)
                        {
                            break;
                        }
                        // increment and continue to add cards to this buffer
                        nCardArrayIdx++;
                    } while (nCardArrayIdx < arrayCardIndices.Length);

                    string sFileName;

                    // NOTE: nCardId at this point should be 1 more than the actual index ... how annoying for export file names...

                    if (!string.IsNullOrEmpty(m_sOverrideStringFormat))
                    {
                        // check for the super override
                        sFileName = CurrentDeck.TranslateFileNameString(m_sOverrideStringFormat, nCardId + 1, nCardCountPadSize);
                    }
                    else if (!string.IsNullOrEmpty(CurrentDeck.CardLayout.exportNameFormat))
                    {
                        // check for the per layout override
                        sFileName = CurrentDeck.TranslateFileNameString(CurrentDeck.CardLayout.exportNameFormat, nCardId + 1, nCardCountPadSize);
                    }
                    else // default
                    {
                        sFileName = CurrentDeck.CardLayout.Name + "_" + (nCardId + 1).ToString(CultureInfo.InvariantCulture).PadLeft(nCardCountPadSize, '0');
                    }
                    
                    try
                    {
                        Save(m_zExportCardBuffer, 
                            m_sExportFolder + sFileName + "." + m_eImageFormat.ToString().ToLower(),
                            m_eImageFormat,
                            CurrentDeck.CardLayout.dpi);
                    }
                    catch (Exception ex)
                    {
						if (m_bDoReporting) ProgressReporter.AddIssue("Invalid Filename or IO error: " + sFileName + " :: " + ex.Message);
						if (m_bDoReporting) ProgressReporter.ThreadSuccess = false;
						if (m_bDoReporting) ProgressReporter.Shutdown();
                        return;
                    }
                }
				if (m_bDoReporting) ProgressReporter.ProgressStep(progressLayoutIdx);
            }

			if (m_bDoReporting) ProgressReporter.ThreadSuccess = true;
			if (m_bDoReporting) ProgressReporter.Shutdown();
        }

        /// <summary>
        /// Gets the array of indices to export
        /// </summary>
        /// <param name="zDeck">The deck to use if no indices are specified</param>
        /// <returns>Array of card indices to export</returns>
        private int[] GetCardIndicesArray(Deck zDeck)
        {
            if (ExportCardIndices != null)
            {
                return ExportCardIndices.Where(i => i < zDeck.CardCount && i >= 0).ToArray();
            }
            else
            {
                return Enumerable.Range(0, zDeck.CardCount).ToArray();
            }
        }

        /// <summary>
        /// Updates the export buffer
        /// </summary>
        /// <param name="nWidth"></param>
        /// <param name="nHeight"></param>
        /// <param name="zGraphics"></param>
        protected override void UpdateBufferBitmap(int nWidth, int nHeight)
        {
            m_zExportCardBuffer?.Dispose();
            m_zExportCardBuffer = new Bitmap(nWidth, nHeight);
        }

        protected void ClearGraphics(Graphics zGraphics)
        {
            zGraphics.Clear(CurrentDeck.CardLayout.exportTransparentBackground ?
                CardMakerConstants.NoColor :
                Color.White);
        }
    }
}
