﻿using Nikse.SubtitleEdit.Controls;
using Nikse.SubtitleEdit.Core;
using Nikse.SubtitleEdit.Core.BluRaySup;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.ContainerFormats.Matroska;
using Nikse.SubtitleEdit.Core.ContainerFormats.TransportStream;
using Nikse.SubtitleEdit.Core.Enums;
using Nikse.SubtitleEdit.Core.Interfaces;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using Nikse.SubtitleEdit.Core.VobSub;
using Nikse.SubtitleEdit.Logic;
using Nikse.SubtitleEdit.Logic.Ocr;
using Nikse.SubtitleEdit.Logic.VideoPlayers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;

namespace Nikse.SubtitleEdit.Forms.BinaryEdit
{
    public partial class BinEdit : Form
    {
        public class Extra
        {
            public bool IsForced { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
            public Bitmap Bitmap { get; set; }
        }

        private readonly Keys _goToLine = UiUtil.GetKeys(Configuration.Settings.Shortcuts.MainEditGoToLineNumber);
        private readonly Keys _mainGeneralGoToNextSubtitle = UiUtil.GetKeys(Configuration.Settings.Shortcuts.GeneralGoToNextSubtitle);
        private readonly Keys _mainGeneralGoToPrevSubtitle = UiUtil.GetKeys(Configuration.Settings.Shortcuts.GeneralGoToPrevSubtitle);
        private List<IBinaryParagraphWithPosition> _binSubtitles;
        private List<Extra> _extra;
        private Subtitle _subtitle;
        private Paragraph _lastPlayParagraph;
        private Point _movableImageMouseDownLocation;
        private string _exportImageFolder;
        private string _videoFileName;
        private double _frameRate;
        private int _screenWidth;
        private int _screenHeight;
        private string _lastSaveHash;
        private string _nOcrFileName;

        public BinEdit(string fileName)
        {
            UiUtil.PreInitialize(this);
            InitializeComponent();
            UiUtil.FixFonts(this);
            UiUtil.FixLargeFonts(this, buttonExportImage);
            UiUtil.InitializeSubtitleFont(subtitleListView1);
            videoPlayerContainer1.Visible = false;
            subtitleListView1.InitializeLanguage(Configuration.Settings.Language.General, Configuration.Settings);
            subtitleListView1.HideColumn(SubtitleListView.SubtitleColumn.CharactersPerSeconds);
            subtitleListView1.HideColumn(SubtitleListView.SubtitleColumn.Actor);
            subtitleListView1.HideColumn(SubtitleListView.SubtitleColumn.WordsPerMinute);
            subtitleListView1.HideColumn(SubtitleListView.SubtitleColumn.Region);
            subtitleListView1.AutoSizeColumns();
            progressBar1.Visible = false;
            OpenBinSubtitle(fileName);
            pictureBoxMovableImage.SizeMode = PictureBoxSizeMode.Normal;
            pictureBoxScreen.SizeMode = PictureBoxSizeMode.StretchImage;
            SetBackgroundImage();
            labelVideoInfo.Text = string.Empty;

            _nOcrFileName = Configuration.Settings.VobSubOcr.LineOcrLastLanguages;
            if (string.IsNullOrEmpty(_nOcrFileName))
            {
                _nOcrFileName = "Latin";
            }
            _nOcrFileName = Path.Combine(Configuration.OcrDirectory, _nOcrFileName + ".nocr");
        }

        private void OpenBinSubtitle(string fileName)
        {
            numericUpDownScreenWidth.Value = 1920;
            numericUpDownScreenHeight.Value = 1080;

            comboBoxFrameRate.Items.Clear();
            comboBoxFrameRate.Items.Add("23.976");
            comboBoxFrameRate.Items.Add("24");
            comboBoxFrameRate.Items.Add("25");
            comboBoxFrameRate.Items.Add("29.97");
            comboBoxFrameRate.Items.Add("30");
            comboBoxFrameRate.Items.Add("50");
            comboBoxFrameRate.Items.Add("59.94");
            comboBoxFrameRate.Items.Add("60");
            comboBoxFrameRate.SelectedIndex = 2;

            var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
            if (FileUtil.IsBluRaySup(fileName))
            {
                var log = new StringBuilder();
                var bluRaySubtitles = BluRaySupParser.ParseBluRaySup(fileName, log);
                _subtitle = new Subtitle();
                _extra = new List<Extra>();
                bool first = true;
                foreach (var s in bluRaySubtitles)
                {
                    if (first)
                    {
                        var bmp = s.GetBitmap();
                        if (bmp != null && bmp.Width > 1 && bmp.Height > 1)
                        {
                            SetResolution(s.GetScreenSize());
                            SetFrameRate(s.FramesPerSecondType);
                            first = false;
                        }
                    }

                    _subtitle.Paragraphs.Add(new Paragraph
                    {
                        StartTime = s.StartTimeCode,
                        EndTime = s.EndTimeCode
                    });

                    var pos = s.GetPosition();
                    _extra.Add(new Extra { IsForced = s.IsForced, X = pos.Left, Y = pos.Top });
                }

                _binSubtitles = new List<IBinaryParagraphWithPosition>(bluRaySubtitles);
                _subtitle.Renumber();
                subtitleListView1.Fill(_subtitle);
            }
            else if (ext == ".mkv" || ext == ".mks")
            {
                if (OpenMatroskaFile(fileName))
                {
                    _subtitle.Renumber();
                    subtitleListView1.Fill(_subtitle);
                }
                else
                {
                    return;
                }
            }
            else if (ext == ".m2ts" || ext == ".ts" || ext == ".mts" || ext == ".rec" || ext == ".mpeg" || ext == ".mpg")
            {
                if (ImportSubtitleFromTransportStream(fileName))
                {
                    _subtitle.Renumber();
                    subtitleListView1.Fill(_subtitle);
                }
                else
                {
                    return;
                }
            }

            if (_subtitle != null)
            {
                subtitleListView1.SelectIndexAndEnsureVisible(_subtitle.GetParagraphOrDefault(0));
            }

            _lastSaveHash = GetStateHash();
        }

        private static Bitmap GetBitmap(IBinaryParagraphWithPosition s)
        {
            if (s is TransportStreamSubtitle)
            {
                var bmp = s.GetBitmap();
                var nikseBitmap = new NikseBitmap(bmp);
                nikseBitmap.CropTopTransparent(0);
                nikseBitmap.CropSidesAndBottom(0, Color.FromArgb(0, 0, 0, 0), true);
                bmp.Dispose();
                return nikseBitmap.GetBitmap();
            }

            return s.GetBitmap();
        }

        private bool ImportSubtitleFromTransportStream(string fileName)
        {
            var tsParser = new TransportStreamParser();
            tsParser.Parse(fileName, null);
            if (tsParser.SubtitlePacketIds.Count == 0)
            {
                return false;  // no image based subtitles found
            }

            tsParser.TeletextSubtitlesLookup.Clear(); // no text subtitles
            if (tsParser.SubtitlePacketIds.Count > 1)
            {
                using (var subChooser = new TransportStreamSubtitleChooser())
                {
                    subChooser.Initialize(tsParser, fileName);
                    if (subChooser.ShowDialog(this) == DialogResult.Cancel)
                    {
                        return false;
                    }

                    if (subChooser.IsTeletext)
                    {
                        return false;
                    }

                    var packetId = tsParser.SubtitlePacketIds[subChooser.SelectedIndex];
                    return LoadTransportStreamSubtitle(tsParser, packetId);
                }
            }

            return LoadTransportStreamSubtitle(tsParser, tsParser.SubtitlePacketIds[0]);
        }

        private bool LoadTransportStreamSubtitle(TransportStreamParser tsParser, int packetId)
        {
            var subtitles = tsParser.GetDvbSubtitles(packetId);
            _subtitle = new Subtitle();
            _extra = new List<Extra>();
            var first = true;
            _binSubtitles = new List<IBinaryParagraphWithPosition>(subtitles);
            foreach (var s in subtitles)
            {
                if (first)
                {
                    var bmpFirst = s.GetBitmap();
                    if (bmpFirst != null && bmpFirst.Width > 1 && bmpFirst.Height > 1)
                    {
                        SetResolution(s.GetScreenSize());
                        //                        SetFrameRate(s.FramesPerSecondType);
                        first = false;
                    }
                }

                _subtitle.Paragraphs.Add(new Paragraph
                {
                    StartTime = s.StartTimeCode,
                    EndTime = s.EndTimeCode
                });

                var pos = s.GetPosition();
                var bmp = s.GetBitmap();
                var nikseBitmap = new NikseBitmap(bmp);
                var y = pos.Top + nikseBitmap.CropTopTransparent(0);
                var x = pos.Left + nikseBitmap.CropSidesAndBottom(0, Color.FromArgb(0, 0, 0, 0), true);
                bmp.Dispose();

                _extra.Add(new Extra { IsForced = s.IsForced, X = x, Y = y });
            }

            return true;
        }

        private bool OpenMatroskaFile(string fileName)
        {
            using (var matroska = new MatroskaFile(fileName))
            {
                if (matroska.IsValid)
                {
                    var subtitleList = matroska.GetTracks(true).Where(p => p.CodecId == "S_HDMV/PGS" || p.CodecId == "S_DVBSUB").ToList();
                    if (subtitleList.Count == 0)
                    {
                        return false; // no bdsup or ts subtitles found
                    }

                    if (subtitleList.Count > 1)
                    {
                        using (var subtitleChooser = new MatroskaSubtitleChooser("mkv"))
                        {
                            subtitleChooser.Initialize(subtitleList);
                            if (subtitleChooser.ShowDialog(this) == DialogResult.OK)
                            {
                                return LoadMatroskaSubtitle(subtitleList[subtitleChooser.SelectedIndex], matroska);
                            }
                        }
                    }
                    else
                    {
                        return LoadMatroskaSubtitle(subtitleList[0], matroska);
                    }
                }
            }

            return false;
        }

        private bool LoadMatroskaSubtitle(MatroskaTrackInfo matroskaSubtitleInfo, MatroskaFile matroska)
        {
            if (matroskaSubtitleInfo.CodecId.Equals("S_HDMV/PGS", StringComparison.OrdinalIgnoreCase))
            {
                return LoadBluRaySubFromMatroska(matroskaSubtitleInfo, matroska);
            }

            if (matroskaSubtitleInfo.CodecId.Equals("S_DVBSUB", StringComparison.OrdinalIgnoreCase))
            {
                return LoadDvbFromMatroska(matroskaSubtitleInfo, matroska);
            }

            return false;
        }

        private bool LoadBluRaySubFromMatroska(MatroskaTrackInfo matroskaSubtitleInfo, MatroskaFile matroska)
        {
            if (matroskaSubtitleInfo.ContentEncodingType == 1)
            {
                return false;
            }

            var sub = matroska.GetSubtitle(matroskaSubtitleInfo.TrackNumber, null);
            var subtitles = new List<BluRaySupParser.PcsData>();
            var log = new StringBuilder();
            var clusterStream = new MemoryStream();
            var lastPalettes = new Dictionary<int, List<PaletteInfo>>();
            var lastBitmapObjects = new Dictionary<int, List<BluRaySupParser.OdsData>>();
            foreach (var p in sub)
            {
                byte[] buffer = p.GetData(matroskaSubtitleInfo);
                if (buffer != null && buffer.Length > 2)
                {
                    clusterStream.Write(buffer, 0, buffer.Length);
                    if (ContainsBluRayStartSegment(buffer))
                    {
                        if (subtitles.Count > 0 && subtitles[subtitles.Count - 1].StartTime == subtitles[subtitles.Count - 1].EndTime)
                        {
                            subtitles[subtitles.Count - 1].EndTime = (long)((p.Start - 1) * 90.0);
                        }

                        clusterStream.Position = 0;
                        var list = BluRaySupParser.ParseBluRaySup(clusterStream, log, true, lastPalettes, lastBitmapObjects);
                        foreach (var sup in list)
                        {
                            sup.StartTime = (long)((p.Start - 1) * 90.0);
                            sup.EndTime = (long)((p.End - 1) * 90.0);
                            subtitles.Add(sup);

                            // fix overlapping
                            if (subtitles.Count > 1 && sub[subtitles.Count - 2].End > sub[subtitles.Count - 1].Start)
                            {
                                subtitles[subtitles.Count - 2].EndTime = subtitles[subtitles.Count - 1].StartTime - 1;
                            }
                        }

                        clusterStream = new MemoryStream();
                    }
                }
                else if (subtitles.Count > 0)
                {
                    var lastSub = subtitles[subtitles.Count - 1];
                    if (lastSub.StartTime == lastSub.EndTime)
                    {
                        lastSub.EndTime = (long)((p.Start - 1) * 90.0);
                        if (lastSub.EndTime - lastSub.StartTime > 1000000)
                        {
                            lastSub.EndTime = lastSub.StartTime;
                        }
                    }
                }
            }

            _subtitle = new Subtitle();
            _extra = new List<Extra>();
            var first = true;
            _binSubtitles = new List<IBinaryParagraphWithPosition>(subtitles);
            foreach (var s in subtitles)
            {
                if (first)
                {
                    var bmp = s.GetBitmap();
                    if (bmp != null && bmp.Width > 1 && bmp.Height > 1)
                    {
                        SetResolution(s.GetScreenSize());
                        SetFrameRate(s.FramesPerSecondType);
                        first = false;
                    }
                }

                _subtitle.Paragraphs.Add(new Paragraph
                {
                    StartTime = s.StartTimeCode,
                    EndTime = s.EndTimeCode
                });

                var pos = s.GetPosition();
                _extra.Add(new Extra { IsForced = s.IsForced, X = pos.Left, Y = pos.Top });
            }

            return true;
        }

        private static bool ContainsBluRayStartSegment(byte[] buffer)
        {
            const int epochStart = 0x80;
            var position = 0;
            while (position + 3 <= buffer.Length)
            {
                var segmentType = buffer[position];
                if (segmentType == epochStart)
                {
                    return true;
                }

                var length = BluRaySupParser.BigEndianInt16(buffer, position + 1) + 3;
                position += length;
            }

            return false;
        }

        private bool LoadDvbFromMatroska(MatroskaTrackInfo matroskaSubtitleInfo, MatroskaFile matroska)
        {
            var sub = matroska.GetSubtitle(matroskaSubtitleInfo.TrackNumber, null);
            var subtitleImages = new List<DvbSubPes>();
            var subtitle = new Subtitle();
            var subtitles = new List<TransportStreamSubtitle>();
            for (int index = 0; index < sub.Count; index++)
            {
                try
                {
                    var msub = sub[index];
                    DvbSubPes pes = null;
                    var data = msub.GetData(matroskaSubtitleInfo);
                    if (data != null && data.Length > 9 && data[0] == 15 && data[1] >= SubtitleSegment.PageCompositionSegment && data[1] <= SubtitleSegment.DisplayDefinitionSegment) // sync byte + segment id
                    {
                        var buffer = new byte[data.Length + 3];
                        Buffer.BlockCopy(data, 0, buffer, 2, data.Length);
                        buffer[0] = 32;
                        buffer[1] = 0;
                        buffer[buffer.Length - 1] = 255;
                        pes = new DvbSubPes(0, buffer);
                    }
                    else if (VobSubParser.IsMpeg2PackHeader(data))
                    {
                        pes = new DvbSubPes(data, Mpeg2Header.Length);
                    }
                    else if (VobSubParser.IsPrivateStream1(data, 0))
                    {
                        pes = new DvbSubPes(data, 0);
                    }
                    else if (data.Length > 9 && data[0] == 32 && data[1] == 0 && data[2] == 14 && data[3] == 16)
                    {
                        pes = new DvbSubPes(0, data);
                    }

                    if (pes == null && subtitle.Paragraphs.Count > 0)
                    {
                        var last = subtitle.Paragraphs[subtitle.Paragraphs.Count - 1];
                        if (last.Duration.TotalMilliseconds < 100)
                        {
                            last.EndTime.TotalMilliseconds = msub.Start;
                            if (last.Duration.TotalMilliseconds > Configuration.Settings.General.SubtitleMaximumDisplayMilliseconds)
                            {
                                last.EndTime.TotalMilliseconds = last.StartTime.TotalMilliseconds + 3000;
                            }
                        }
                    }

                    if (pes?.PageCompositions != null && pes.PageCompositions.Any(p => p.Regions.Count > 0))
                    {
                        subtitleImages.Add(pes);
                        subtitle.Paragraphs.Add(new Paragraph(string.Empty, msub.Start, msub.End));
                        subtitles.Add(new TransportStreamSubtitle { Pes = pes });
                    }
                }
                catch
                {
                    // continue
                }
            }

            if (subtitleImages.Count == 0)
            {
                return false;
            }

            for (int index = 0; index < subtitle.Paragraphs.Count; index++)
            {
                var p = subtitle.Paragraphs[index];
                if (p.Duration.TotalMilliseconds < 200)
                {
                    p.EndTime.TotalMilliseconds = p.StartTime.TotalMilliseconds + 3000;
                }

                var next = subtitle.GetParagraphOrDefault(index + 1);
                if (next != null && next.StartTime.TotalMilliseconds < p.EndTime.TotalMilliseconds)
                {
                    p.EndTime.TotalMilliseconds = next.StartTime.TotalMilliseconds - Configuration.Settings.General.MinimumMillisecondsBetweenLines;
                }

                subtitles[index].StartMilliseconds = (ulong)Math.Round(p.StartTime.TotalMilliseconds);
                subtitles[index].EndMilliseconds = (ulong)Math.Round(p.EndTime.TotalMilliseconds);
            }

            _subtitle = new Subtitle();
            _extra = new List<Extra>();
            var first = true;
            _binSubtitles = new List<IBinaryParagraphWithPosition>(subtitles);
            foreach (var s in _binSubtitles)
            {
                if (first)
                {
                    var bmp = s.GetBitmap();
                    if (bmp != null && bmp.Width > 1 && bmp.Height > 1)
                    {
                        SetFrameRate(0x10);
                        SetResolution(s.GetScreenSize());
                        first = false;
                    }
                }

                _subtitle.Paragraphs.Add(new Paragraph
                {
                    StartTime = s.StartTimeCode,
                    EndTime = s.EndTimeCode
                });

                var pos = s.GetPosition();
                _extra.Add(new Extra { IsForced = false, X = pos.Left, Y = pos.Top });
            }

            return true;
        }

        private void SetBackgroundImage()
        {
            var screenBmp = new Bitmap((int)numericUpDownScreenWidth.Value, (int)numericUpDownScreenHeight.Value);
            using (var g = Graphics.FromImage(screenBmp))
            {
                using (var brush = new SolidBrush(Color.Black))
                {
                    g.FillRectangle(brush, 0, 0, screenBmp.Width, screenBmp.Height);
                }
            }
            pictureBoxScreen.Image = screenBmp;
        }

        private void SetResolution(Size bmpSize)
        {
            numericUpDownScreenWidth.Value = bmpSize.Width;
            numericUpDownScreenHeight.Value = bmpSize.Height;
        }

        private void SetFrameRate(int fpsId)
        {
            switch (fpsId)
            {
                case 0x20:
                    comboBoxFrameRate.SelectedIndex = 1;
                    break;
                case 0x30:
                    comboBoxFrameRate.SelectedIndex = 2;
                    break;
                case 0x40:
                    comboBoxFrameRate.SelectedIndex = 3;
                    break;
                case 0x50:
                    comboBoxFrameRate.SelectedIndex = 4;
                    break;
                case 0x60:
                    comboBoxFrameRate.SelectedIndex = 5;
                    break;
                case 0x70:
                    comboBoxFrameRate.SelectedIndex = 6;
                    break;
                case 0x80:
                    comboBoxFrameRate.SelectedIndex = 7;
                    break;
                default:
                    comboBoxFrameRate.SelectedIndex = 0;
                    break;
            }
        }

        private void insertToolStripMenuItem_Click(object sender, EventArgs e)
        {
            InsertParagraph(false);
        }

        private void insertAfterToolStripMenuItem_Click(object sender, EventArgs e)
        {
            InsertParagraph(true);
        }

        private void InsertParagraph(bool after)
        {
            var p = new Paragraph();

            if (subtitleListView1.SelectedItems.Count < 1)
            {
                p.EndTime.TotalMilliseconds = p.StartTime.TotalMilliseconds + Configuration.Settings.General.NewEmptyDefaultMs;
                _subtitle.Paragraphs.Add(p);
                _extra.Add(new Extra { Bitmap = new Bitmap(2, 2), IsForced = checkBoxIsForced.Checked, X = (int)numericUpDownX.Value, Y = (int)numericUpDownY.Value });
                subtitleListView1.Fill(_subtitle);
                subtitleListView1.SelectIndexAndEnsureVisible(0);
                return;
            }

            var idx = subtitleListView1.SelectedItems[0].Index;
            if (after)
            {
                idx++;
            }

            if (_binSubtitles != null)
            {
                _subtitle.Paragraphs.Insert(idx, p);
                var extra = new Extra { Bitmap = new Bitmap(2, 2), IsForced = checkBoxIsForced.Checked, X = (int)numericUpDownX.Value, Y = (int)numericUpDownY.Value };
                _extra.Insert(idx, extra);
                var pre = _subtitle.GetParagraphOrDefault(idx - 1);
                if (pre != null)
                {
                    p.StartTime.TotalMilliseconds = pre.EndTime.TotalMilliseconds + Configuration.Settings.General.MinimumMillisecondsBetweenLines;
                }

                p.EndTime.TotalMilliseconds = p.StartTime.TotalMilliseconds + Configuration.Settings.General.NewEmptyDefaultMs;
                _binSubtitles.Insert(idx, new BluRaySupParser.PcsData());
                subtitleListView1.Fill(_subtitle);
                subtitleListView1.SelectIndexAndEnsureVisible(idx);
            }
        }

        private void subtitleListView1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (subtitleListView1.SelectedItems.Count < 1)
            {
                timeUpDownStartTime.TimeCode = new TimeCode();
                timeUpDownEndTime.TimeCode = new TimeCode();
                numericUpDownX.Value = 0;
                numericUpDownY.Value = 0;
                groupBoxCurrent.Text = string.Empty;
                if (subtitleListView1.Items.Count == 0)
                {
                    pictureBoxMovableImage.Hide();
                }
                return;
            }

            var idx = subtitleListView1.SelectedItems[0].Index;
            var p = _subtitle.Paragraphs[idx];
            groupBoxCurrent.Text = $"{(idx + 1)} / {(_subtitle.Paragraphs.Count + 1)}";
            if (videoPlayerContainer1.VideoPlayer != null && videoPlayerContainer1.IsPaused)
            {
                videoPlayerContainer1.CurrentPosition = p.StartTime.TotalSeconds;
            }

            if (_binSubtitles != null)
            {
                var sub = _binSubtitles[idx];
                var extra = _extra[idx];
                timeUpDownStartTime.TimeCode = new TimeCode(p.StartTime.TotalMilliseconds);
                timeUpDownEndTime.TimeCode = new TimeCode(p.EndTime.TotalMilliseconds);
                checkBoxIsForced.Checked = extra.IsForced;
                numericUpDownX.Value = extra.X;
                numericUpDownY.Value = extra.Y;
                var bmp = extra.Bitmap != null ? (Bitmap)extra.Bitmap.Clone() : GetBitmap(sub);
                labelCurrentSize.Text = string.Format("Size: {0}x{1}", bmp.Width, bmp.Height);
                ShowCurrentScaledImage(bmp, extra);
            }
        }

        private void deleteToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (subtitleListView1.SelectedItems.Count < 1)
            {
                return;
            }

            var removeIndices = new List<int>();
            System.Collections.IList list = subtitleListView1.SelectedIndices;
            for (int i = 0; i < subtitleListView1.SelectedIndices.Count; i++)
            {
                int index = (int)list[i];
                removeIndices.Add(index);
            }

            var idx = subtitleListView1.SelectedItems[0].Index;
            removeIndices = removeIndices.OrderByDescending(p => p).ToList();
            foreach (var removeIndex in removeIndices)
            {
                _binSubtitles?.RemoveAt(removeIndex);
                _extra.RemoveAt(removeIndex);
                _subtitle.Paragraphs.RemoveAt(removeIndex);
            }
            _subtitle.Renumber();
            subtitleListView1.Fill(_subtitle);

            if (idx >= _subtitle.Paragraphs.Count && idx > 0)
            {
                idx--;
            }

            if (idx >= 0)
            {
                subtitleListView1.SelectIndexAndEnsureVisible(_subtitle.GetParagraphOrDefault(idx));
            }
        }

        private void subtitleListView1_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Modifiers == Keys.None && e.KeyData == Keys.Delete)
            {
                deleteToolStripMenuItem_Click(sender, e);
                e.SuppressKeyPress = true;
                subtitleListView1.Focus();
            }
            else if (e.Modifiers == Keys.Control && e.KeyCode == Keys.A)
            {
                subtitleListView1.SelectedIndexChanged -= subtitleListView1_SelectedIndexChanged;
                subtitleListView1.SelectAll();
                subtitleListView1.SelectedIndexChanged += subtitleListView1_SelectedIndexChanged;
                e.SuppressKeyPress = true;
            }
            else if (e.Modifiers == Keys.Control && e.KeyCode == Keys.D)
            {
                subtitleListView1.SelectedIndexChanged -= subtitleListView1_SelectedIndexChanged;
                subtitleListView1.SelectFirstSelectedItemOnly();
                subtitleListView1.SelectedIndexChanged += subtitleListView1_SelectedIndexChanged;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.I && e.Modifiers == (Keys.Control | Keys.Shift)) //InverseSelection
            {
                subtitleListView1.SelectedIndexChanged -= subtitleListView1_SelectedIndexChanged;
                subtitleListView1.InverseSelection();
                subtitleListView1.SelectedIndexChanged += subtitleListView1_SelectedIndexChanged;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Home && e.Modifiers == Keys.Control) // Go to first sub
            {
                subtitleListView1.SelectIndexAndEnsureVisible(0, true);
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.End && e.Modifiers == Keys.Control) // Go to last sub
            {
                if (subtitleListView1.Items.Count >= 0)
                {
                    subtitleListView1.SelectIndexAndEnsureVisible(subtitleListView1.Items.Count - 1, true);
                }

                e.SuppressKeyPress = true;
            }
            else if (subtitleListView1.Visible && subtitleListView1.Items.Count > 0 && e.KeyData == _goToLine)
            {
                GoToLineNumber();
            }
            else if (_mainGeneralGoToNextSubtitle == e.KeyData || (e.KeyCode == Keys.Down && e.Modifiers == Keys.Alt))
            {
                int selectedIndex = 0;
                if (subtitleListView1.SelectedItems.Count > 0)
                {
                    selectedIndex = subtitleListView1.SelectedItems[0].Index;
                    selectedIndex++;
                }
                subtitleListView1.SelectIndexAndEnsureVisible(selectedIndex);
            }
            else if (_mainGeneralGoToPrevSubtitle == e.KeyData || (e.KeyCode == Keys.Up && e.Modifiers == Keys.Alt))
            {
                int selectedIndex = 0;
                if (subtitleListView1.SelectedItems.Count > 0)
                {
                    selectedIndex = subtitleListView1.SelectedItems[0].Index;
                    selectedIndex--;
                }
                subtitleListView1.SelectIndexAndEnsureVisible(selectedIndex);
            }
        }

        private void GoToLineNumber()
        {
            using (var goToLine = new GoToLine())
            {
                goToLine.Initialize(1, subtitleListView1.Items.Count);
                if (goToLine.ShowDialog(this) == DialogResult.OK)
                {
                    subtitleListView1.SelectNone();
                    subtitleListView1.Items[goToLine.LineNumber - 1].Selected = true;
                    subtitleListView1.Items[goToLine.LineNumber - 1].EnsureVisible();
                    subtitleListView1.Items[goToLine.LineNumber - 1].Focused = true;
                }
            }
        }

        private int MillisecondsToFramesMaxFrameRate(double milliseconds)
        {
            int frames = (int)Math.Round(milliseconds / (1000.0 / _frameRate));
            if (frames >= _frameRate)
            {
                frames = (int)(_frameRate - 0.01);
            }

            return frames;
        }

        private string ToHHMMSSFF(TimeCode timeCode)
        {
            return $"{timeCode.Hours:00}:{timeCode.Minutes:00}:{timeCode.Seconds:00}:{MillisecondsToFramesMaxFrameRate(timeCode.Milliseconds):00}";
        }

        private void WriteBdnXmlParagraph(Bitmap bitmap, StringBuilder sb, string path, int i, Extra extra, Paragraph p)
        {
            string numberString = $"{i:0000}";
            string fileName = Path.Combine(path, numberString + ".png");
            bitmap.Save(fileName, ImageFormat.Png);
            sb.AppendLine("<Event InTC=\"" + ToHHMMSSFF(p.StartTime) + "\" OutTC=\"" + ToHHMMSSFF(p.EndTime) + "\" Forced=\"" + extra.IsForced.ToString().ToLowerInvariant() + "\">");
            sb.AppendLine("  <Graphic Width=\"" + bitmap.Width.ToString(CultureInfo.InvariantCulture) + "\" Height=\"" +
                          bitmap.Height.ToString(CultureInfo.InvariantCulture) + "\" X=\"" + extra.X.ToString(CultureInfo.InvariantCulture) + "\" Y=\"" + extra.Y.ToString(CultureInfo.InvariantCulture) +
                          "\">" + numberString + ".png</Graphic>");
            sb.AppendLine("</Event>");
        }

        private static string FormatUtf8Xml(XmlDocument doc)
        {
            var sb = new StringBuilder();
            using (var writer = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 }))
            {
                doc.Save(writer);
            }
            return sb.ToString().Replace(" encoding=\"utf-16\"", " encoding=\"utf-8\"").Trim(); // "replace hack" due to missing encoding (encoding only works if it's the only parameter...)
        }

        private void WriteBdnXmlFile(StringBuilder sb, string fileName, int count)
        {
            string videoFormat = "1080p";
            if (_screenWidth == 1920 && _screenHeight == 1080)
            {
                videoFormat = "1080p";
            }
            else if (_screenWidth == 1280 && _screenHeight == 720)
            {
                videoFormat = "720p";
            }
            else if (_screenWidth == 848 && _screenHeight == 480)
            {
                videoFormat = "480p";
            }
            else if (_screenWidth > 0 && _screenHeight > 0)
            {
                videoFormat = _screenWidth + "x" + _screenHeight;
            }

            var doc = new XmlDocument();
            Paragraph first = _subtitle.Paragraphs[0];
            Paragraph last = _subtitle.Paragraphs[_subtitle.Paragraphs.Count - 1];
            doc.LoadXml("<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + Environment.NewLine +
                        "<BDN Version=\"0.93\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:noNamespaceSchemaLocation=\"BD-03-006-0093b BDN File Format.xsd\">" + Environment.NewLine +
                        "<Description>" + Environment.NewLine +
                        "<Name Title=\"subtitle_exp\" Content=\"\"/>" + Environment.NewLine +
                        "<Language Code=\"eng\"/>" + Environment.NewLine +
                        "<Format VideoFormat=\"" + videoFormat + "\" FrameRate=\"" + _frameRate.ToString(CultureInfo.InvariantCulture) + "\" DropFrame=\"False\"/>" + Environment.NewLine +
                        "<Events Type=\"Graphic\" FirstEventInTC=\"" + ToHHMMSSFF(first.StartTime) + "\" LastEventOutTC=\"" + ToHHMMSSFF(last.EndTime) + "\" NumberofEvents=\"" + count.ToString(CultureInfo.InvariantCulture) + "\"/>" + Environment.NewLine +
                        "</Description>" + Environment.NewLine +
                        "<Events>" + Environment.NewLine +
                        "</Events>" + Environment.NewLine +
                        "</BDN>");
            XmlNode events = doc.DocumentElement.SelectSingleNode("Events");
            doc.PreserveWhitespace = true;
            events.InnerXml = sb.ToString();
            File.WriteAllText(fileName, FormatUtf8Xml(doc), Encoding.UTF8);
        }

        private void saveFileAsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            saveFileDialog1.Title = Configuration.Settings.Language.ExportPngXml.SaveBluRraySupAs;
            saveFileDialog1.DefaultExt = "*.sup";
            saveFileDialog1.AddExtension = true;
            saveFileDialog1.Filter = Configuration.Settings.Language.Main.BluRaySupFiles + "|*.sup|BDN xml/png|*.xml";
            if (saveFileDialog1.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            _lastSaveHash = GetStateHash();

            progressBar1.Value = 0;
            progressBar1.Maximum = _subtitle.Paragraphs.Count;
            progressBar1.Style = ProgressBarStyle.Blocks;
            progressBar1.Visible = true;
            if (_binSubtitles != null)
            {
                Cursor = Cursors.WaitCursor;
                var bw = new BackgroundWorker { WorkerReportsProgress = true };
                bw.RunWorkerCompleted += (o, args) =>
                {
                    Cursor = Cursors.Default;
                    progressBar1.Visible = false;

                    var folderName = Path.GetDirectoryName(saveFileDialog1.FileName);
                    var text = string.Format(Configuration.Settings.Language.Main.SavedSubtitleX, saveFileDialog1.FileName);
                    if (saveFileDialog1.FilterIndex == 2)
                    {
                        text = string.Format(Configuration.Settings.Language.ExportPngXml.XImagesSavedInY, _extra.Count, folderName);
                    }

                    using (var f = new ExportPngXmlDialogOpenFolder(text, folderName))
                    {
                        f.ShowDialog(this);
                    }
                };
                bw.ProgressChanged += (o, args) =>
                {
                    progressBar1.Value = args.ProgressPercentage;
                };
                bw.DoWork += (o, args) =>
                {
                    if (saveFileDialog1.FilterIndex == 1)
                    {
                        using (var binarySubtitleFile = new FileStream(args.Argument.ToString(), FileMode.Create))
                        {
                            for (var index = 0; index < _subtitle.Paragraphs.Count; index++)
                            {
                                bw.ReportProgress(index);
                                var p = _subtitle.Paragraphs[index];
                                var bd = _binSubtitles[index];
                                var extra = _extra[index];
                                var bmp = extra.Bitmap ?? GetBitmap(bd);
                                var brSub = new BluRaySupPicture
                                {
                                    StartTime = (long)p.StartTime.TotalMilliseconds,
                                    EndTime = (long)p.EndTime.TotalMilliseconds,
                                    Width = (int)numericUpDownScreenWidth.Value,
                                    Height = (int)numericUpDownScreenHeight.Value,
                                    CompositionNumber = p.Number * 2,
                                    IsForced = extra.IsForced,
                                };
                                var buffer = BluRaySupPicture.CreateSupFrame(brSub, bmp, 25, 0, 0, 0, new Point(extra.X, extra.Y));
                                if (extra.Bitmap == null)
                                {
                                    bmp.Dispose();
                                }

                                binarySubtitleFile.Write(buffer, 0, buffer.Length);
                            }
                        }
                    }
                    else if (saveFileDialog1.FilterIndex == 2)
                    {
                        var path = Path.GetDirectoryName(saveFileDialog1.FileName);
                        var sb = new StringBuilder();
                        for (var index = 0; index < _subtitle.Paragraphs.Count; index++)
                        {
                            bw.ReportProgress(index);
                            var p = _subtitle.Paragraphs[index];
                            var bd = _binSubtitles[index];
                            var extra = _extra[index];
                            var bmp = extra.Bitmap ?? GetBitmap(bd);
                            WriteBdnXmlParagraph(bmp, sb, path, (index + 1), extra, p);
                            bmp.Dispose();
                        }
                        WriteBdnXmlFile(sb, saveFileDialog1.FileName, _extra.Count);
                    }
                };
                bw.RunWorkerAsync(saveFileDialog1.FileName);
            }
        }

        private string GetStateHash()
        {
            int extraHash = 17;
            int subtitleHash = 17;
            unchecked // Overflow is fine, just wrap
            {
                if (_extra != null)
                {
                    foreach (var extra in _extra)
                    {
                        extraHash = extraHash * 23 + extra.X.GetHashCode();
                        extraHash = extraHash * 23 + extra.Y.GetHashCode();
                        extraHash = extraHash * 23 + extra.IsForced.GetHashCode();
                        if (extra.Bitmap != null)
                        {
                            extraHash = extraHash * 23 + extra.Bitmap.GetHashCode();
                        }
                    }
                }


                if (_subtitle != null)
                {
                    foreach (var p in _subtitle.Paragraphs)
                    {
                        subtitleHash = subtitleHash * 23 + p.StartTime.TotalMilliseconds.GetHashCode();
                        subtitleHash = subtitleHash * 23 + p.EndTime.TotalMilliseconds.GetHashCode();
                    }
                }
            }

            return extraHash.ToString() + subtitleHash;
        }

        private void openFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            openFileDialog1.Title = Configuration.Settings.Language.Main.OpenBluRaySupFile;
            openFileDialog1.FileName = string.Empty;
            openFileDialog1.Filter = Configuration.Settings.Language.Main.BluRaySupFiles + "|*.sup|" +
                                     "Matroska|*.mkv;*.mks|" +
                                     "Transport stream|*.ts;*.m2ts;*.mts;*.rec;*.mpeg;*.mpg";
            if (openFileDialog1.ShowDialog(this) == DialogResult.OK)
            {
                OpenBinSubtitle(openFileDialog1.FileName);
            }
        }

        private void closeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.OK;
        }

        private void numericUpDownX_ValueChanged(object sender, EventArgs e)
        {
            if (subtitleListView1.SelectedItems.Count < 1)
            {
                return;
            }

            var idx = subtitleListView1.SelectedItems[0].Index;
            if (idx < 0)
            {
                return;
            }

            var extra = _extra[idx];
            extra.X = (int)numericUpDownX.Value;

            var widthAspect = pictureBoxScreen.Width / numericUpDownScreenWidth.Value;
            pictureBoxMovableImage.Left = (int)Math.Round(extra.X * widthAspect);
        }

        private void numericUpDownY_ValueChanged(object sender, EventArgs e)
        {
            if (subtitleListView1.SelectedItems.Count < 1)
            {
                return;
            }

            var idx = subtitleListView1.SelectedItems[0].Index;
            if (idx < 0)
            {
                return;
            }

            var extra = _extra[idx];
            extra.Y = (int)numericUpDownY.Value;

            var heightAspect = pictureBoxScreen.Height / numericUpDownScreenHeight.Value;
            pictureBoxMovableImage.Top = (int)Math.Round(extra.Y * heightAspect);
        }

        private void checkBoxIsForced_CheckedChanged(object sender, EventArgs e)
        {
            if (subtitleListView1.SelectedItems.Count < 1)
            {
                return;
            }

            var idx = subtitleListView1.SelectedItems[0].Index;
            if (idx < 0)
            {
                return;
            }

            var extra = _extra[idx];
            extra.IsForced = checkBoxIsForced.Checked;
        }

        private void BinEdit_Shown(object sender, EventArgs e)
        {
            timeUpDownStartTime.MaskedTextBox.TextChanged += (o, args) =>
            {
                if (subtitleListView1.SelectedItems.Count < 1)
                {
                    return;
                }

                var idx = subtitleListView1.SelectedItems[0].Index;
                var p = _subtitle.GetParagraphOrDefault(idx);
                if (p == null)
                {
                    return;
                }

                p.StartTime.TotalMilliseconds = timeUpDownStartTime.TimeCode.TotalMilliseconds;
                subtitleListView1.SetStartTimeAndDuration(idx, p, _subtitle.GetParagraphOrDefault(idx + 1), _subtitle.GetParagraphOrDefault(idx - 1));
                subtitleListView1.SyntaxColorLine(_subtitle.Paragraphs, idx, p);
            };
            timeUpDownEndTime.MaskedTextBox.TextChanged += (o, args) =>
            {
                if (subtitleListView1.SelectedItems.Count < 1)
                {
                    return;
                }

                var idx = subtitleListView1.SelectedItems[0].Index;
                var p = _subtitle.GetParagraphOrDefault(idx);
                if (p == null)
                {
                    return;
                }

                p.EndTime.TotalMilliseconds = timeUpDownEndTime.TimeCode.TotalMilliseconds;
                subtitleListView1.SetStartTimeAndDuration(idx, p, _subtitle.GetParagraphOrDefault(idx + 1), _subtitle.GetParagraphOrDefault(idx - 1));
                subtitleListView1.SyntaxColorLine(_subtitle.Paragraphs, idx, p);
            };
        }

        private void adjustAllTimesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (subtitleListView1.SelectedItems.Count < 1)
            {
                return;
            }

            using (var form = new ShowEarlierLater())
            {
                form.Initialize(ShowEarlierOrLater, subtitleListView1.SelectedItems.Count > 1);
                form.ShowDialog(this);
            }
        }

        public void ShowEarlierOrLater(double adjustMilliseconds, SelectionChoice selection)
        {
            if (subtitleListView1.SelectedItems.Count < 1)
            {
                return;
            }

            if (selection == SelectionChoice.AllLines)
            {
                for (int i = 0; i < _subtitle.Paragraphs.Count; i++)
                {
                    ShowEarlierOrLaterParagraph(adjustMilliseconds, i);
                }
            }
            else if (selection == SelectionChoice.SelectionAndForward)
            {
                for (int i = subtitleListView1.SelectedItems[0].Index; i < _subtitle.Paragraphs.Count; i++)
                {
                    ShowEarlierOrLaterParagraph(adjustMilliseconds, i);
                }
            }
            else if (selection == SelectionChoice.SelectionOnly)
            {
                for (int i = subtitleListView1.SelectedItems[0].Index; i < _subtitle.Paragraphs.Count; i++)
                {
                    if (subtitleListView1.Items[i].Selected)
                    {
                        ShowEarlierOrLaterParagraph(adjustMilliseconds, i);
                    }
                }
            }

            var idx = subtitleListView1.SelectedItems[0].Index;
            subtitleListView1.Fill(_subtitle);
            subtitleListView1.SelectIndexAndEnsureVisible(_subtitle.GetParagraphOrDefault(idx));
        }

        private void ShowEarlierOrLaterParagraph(double adjustMilliseconds, int i)
        {
            var p = _subtitle.GetParagraphOrDefault(i);
            if (p != null && !p.StartTime.IsMaxTime)
            {
                p.StartTime.TotalMilliseconds += adjustMilliseconds;
                p.EndTime.TotalMilliseconds += adjustMilliseconds;
                subtitleListView1.SetStartTimeAndDuration(i, p, _subtitle.GetParagraphOrDefault(i + 1), _subtitle.GetParagraphOrDefault(i - 1));
            }
        }

        private void importTimeCodesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (_subtitle.Paragraphs.Count < 1)
            {
                return;
            }

            openFileDialog1.Title = Configuration.Settings.Language.General.OpenSubtitle;
            openFileDialog1.FileName = string.Empty;
            openFileDialog1.Filter = UiUtil.SubtitleExtensionFilter.Value;
            if (openFileDialog1.ShowDialog(this) == DialogResult.OK)
            {
                var timeCodeSubtitle = new Subtitle();
                SubtitleFormat format = null;

                if (openFileDialog1.FileName.EndsWith(".sup", StringComparison.OrdinalIgnoreCase) &&
                    FileUtil.IsBluRaySup(openFileDialog1.FileName))
                {
                    var log = new StringBuilder();
                    var subtitles = BluRaySupParser.ParseBluRaySup(openFileDialog1.FileName, log);
                    if (subtitles.Count > 0)
                    {
                        foreach (var sup in subtitles)
                        {
                            timeCodeSubtitle.Paragraphs.Add(new Paragraph(sup.StartTimeCode, sup.EndTimeCode, string.Empty));
                        }

                        format = new SubRip(); // just to set format to something
                    }
                }

                if (format == null)
                {
                    format = timeCodeSubtitle.LoadSubtitle(openFileDialog1.FileName, out var encoding, null);
                }

                if (format == null)
                {
                    var formats = SubtitleFormat.GetBinaryFormats(true).Union(SubtitleFormat.GetTextOtherFormats()).Union(new SubtitleFormat[]
                    {
                        new TimeCodesOnly1(),
                        new TimeCodesOnly2()
                    }).ToArray();
                    format = SubtitleFormat.LoadSubtitleFromFile(formats, openFileDialog1.FileName, timeCodeSubtitle);
                }

                if (format == null)
                {
                    return;
                }

                if (timeCodeSubtitle.Paragraphs.Count != _subtitle.Paragraphs.Count)
                {
                    var text = string.Format(Configuration.Settings.Language.Main.ImportTimeCodesDifferentNumberOfLinesWarning, timeCodeSubtitle.Paragraphs.Count, _subtitle.Paragraphs.Count);
                    if (MessageBox.Show(this, text, Text, MessageBoxButtons.YesNoCancel) != DialogResult.Yes)
                    {
                        return;
                    }
                }

                int count = 0;
                for (int i = 0; i < timeCodeSubtitle.Paragraphs.Count; i++)
                {
                    var existing = _subtitle.GetParagraphOrDefault(i);

                    var newTimeCode = timeCodeSubtitle.GetParagraphOrDefault(i);
                    if (existing == null || newTimeCode == null)
                    {
                        break;
                    }

                    existing.StartTime.TotalMilliseconds = newTimeCode.StartTime.TotalMilliseconds;
                    existing.EndTime.TotalMilliseconds = newTimeCode.EndTime.TotalMilliseconds;
                    count++;
                }

                MessageBox.Show(string.Format(Configuration.Settings.Language.Main.TimeCodeImportedFromXY, Path.GetFileName(openFileDialog1.FileName), count));
                var idx = subtitleListView1.SelectedItems[0].Index;
                subtitleListView1.Fill(_subtitle);
                subtitleListView1.SelectIndexAndEnsureVisible(_subtitle.GetParagraphOrDefault(idx));
            }
        }

        private void changeFrameRateToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (subtitleListView1.SelectedItems.Count < 1)
            {
                return;
            }

            using (var changeFrameRate = new ChangeFrameRate())
            {
                changeFrameRate.Initialize(comboBoxFrameRate.Text);
                if (changeFrameRate.ShowDialog(this) == DialogResult.OK)
                {
                    var oldFrameRate = changeFrameRate.OldFrameRate;
                    var newFrameRate = changeFrameRate.NewFrameRate;
                    _subtitle.ChangeFrameRate(oldFrameRate, newFrameRate);
                    MessageBox.Show(string.Format(Configuration.Settings.Language.Main.FrameRateChangedFromXToY, oldFrameRate, newFrameRate));
                    var idx = subtitleListView1.SelectedItems[0].Index;
                    subtitleListView1.Fill(_subtitle);
                    subtitleListView1.SelectIndexAndEnsureVisible(_subtitle.GetParagraphOrDefault(idx));
                }
            }
        }

        private void changeSpeedToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (subtitleListView1.SelectedItems.Count < 1)
            {
                return;
            }

            using (var form = new ChangeSpeedInPercent(subtitleListView1.SelectedItems.Count))
            {
                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    if (form.AdjustAllLines)
                    {
                        _subtitle = form.AdjustAllParagraphs(_subtitle);
                    }
                    else
                    {
                        foreach (int index in subtitleListView1.SelectedIndices)
                        {
                            var p = _subtitle.GetParagraphOrDefault(index);
                            if (p != null)
                            {
                                form.AdjustParagraph(p);
                            }
                        }
                    }

                    var idx = subtitleListView1.SelectedItems[0].Index;
                    subtitleListView1.Fill(_subtitle);
                    subtitleListView1.SelectIndexAndEnsureVisible(_subtitle.GetParagraphOrDefault(idx));
                }
            }
        }

        private void pictureBoxMovableImage_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _movableImageMouseDownLocation = e.Location;
            }
        }

        private void pictureBoxMovableImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (subtitleListView1.SelectedItems.Count < 1)
            {
                return;
            }

            if (e.Button == MouseButtons.Left)
            {
                pictureBoxMovableImage.Left = e.X + pictureBoxMovableImage.Left - _movableImageMouseDownLocation.X;
                pictureBoxMovableImage.Top = e.Y + pictureBoxMovableImage.Top - _movableImageMouseDownLocation.Y;

                var idx = subtitleListView1.SelectedItems[0].Index;
                var extra = _extra[idx];
                var widthAspect = numericUpDownScreenWidth.Value / pictureBoxScreen.Width;
                var heightAspect = numericUpDownScreenHeight.Value / pictureBoxScreen.Height;
                extra.X = (int)Math.Round(pictureBoxMovableImage.Left * widthAspect);
                extra.Y = (int)Math.Round(pictureBoxMovableImage.Top * heightAspect);
                numericUpDownX.Value = extra.X;
                numericUpDownY.Value = extra.Y;
            }
        }

        private void BinEdit_ResizeEnd(object sender, EventArgs e)
        {
            subtitleListView1_SelectedIndexChanged(sender, e);
        }

        private void buttonImportImage_Click(object sender, EventArgs e)
        {
            if (subtitleListView1.SelectedItems.Count < 1)
            {
                return;
            }

            openFileDialog1.Title = "Open...";
            openFileDialog1.FileName = string.Empty;
            openFileDialog1.Filter = "PNG image|*.png|BMP image|*.bmp|GIF image|*.gif|TIFF image|*.tiff";
            openFileDialog1.FilterIndex = 0;
            if (!string.IsNullOrEmpty(_exportImageFolder))
            {
                openFileDialog1.InitialDirectory = _exportImageFolder;
            }

            var result = openFileDialog1.ShowDialog(this);
            if (result == DialogResult.OK)
            {
                var idx = subtitleListView1.SelectedItems[0].Index;
                _extra[idx].Bitmap = new Bitmap(openFileDialog1.FileName);
                subtitleListView1_SelectedIndexChanged(sender, e);
            }
        }

        private void buttonExportImage_Click(object sender, EventArgs e)
        {
            if (subtitleListView1.SelectedItems.Count < 1)
            {
                return;
            }

            var idx = subtitleListView1.SelectedItems[0].Index;
            saveFileDialog1.Title = Configuration.Settings.Language.VobSubOcr.SaveSubtitleImageAs;
            saveFileDialog1.AddExtension = true;
            saveFileDialog1.FileName = "Image" + (idx + 1);
            saveFileDialog1.Filter = "PNG image|*.png|BMP image|*.bmp|GIF image|*.gif|TIFF image|*.tiff";
            saveFileDialog1.FilterIndex = 0;

            var result = saveFileDialog1.ShowDialog(this);
            if (result == DialogResult.OK)
            {
                _exportImageFolder = Path.GetDirectoryName(saveFileDialog1.FileName);
                var extra = _extra[idx];
                var bmp = extra.Bitmap;
                if (bmp == null && _binSubtitles != null)
                {
                    bmp = GetBitmap(_binSubtitles[idx]);
                }

                if (bmp == null)
                {
                    MessageBox.Show("No image!");
                    return;
                }

                try
                {
                    if (saveFileDialog1.FilterIndex == 0)
                    {
                        bmp.Save(saveFileDialog1.FileName, ImageFormat.Png);
                    }
                    else if (saveFileDialog1.FilterIndex == 1)
                    {
                        bmp.Save(saveFileDialog1.FileName);
                    }
                    else if (saveFileDialog1.FilterIndex == 2)
                    {
                        bmp.Save(saveFileDialog1.FileName, ImageFormat.Gif);
                    }
                    else
                    {
                        bmp.Save(saveFileDialog1.FileName, ImageFormat.Tiff);
                    }
                }
                catch (Exception exception)
                {
                    MessageBox.Show(exception.Message);
                }
                finally
                {
                    bmp.Dispose();
                }
            }
        }

        private void centerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (subtitleListView1.SelectedItems.Count < 1)
            {
                return;
            }

            var idx = subtitleListView1.SelectedItems[0].Index;
            var sub = _binSubtitles[idx];
            var extra = _extra[idx];
            var bmp = extra.Bitmap != null ? (Bitmap)extra.Bitmap.Clone() : sub.GetBitmap();
            numericUpDownX.Value = (int)Math.Round(numericUpDownScreenWidth.Value / 2.0m - bmp.Width / 2.0m);
        }

        private void insertSubtitleAfterThisLineToolStripMenuItem_Click_1(object sender, EventArgs e)
        {
            if (subtitleListView1.SelectedItems.Count < 1)
            {
                return;
            }

            openFileDialog1.Title = Configuration.Settings.Language.Main.OpenBluRaySupFile;
            openFileDialog1.FileName = string.Empty;
            openFileDialog1.Filter = Configuration.Settings.Language.Main.BluRaySupFiles + "|*.sup";
            if (openFileDialog1.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            if (!FileUtil.IsBluRaySup(openFileDialog1.FileName))
            {
                return;
            }

            var idx = subtitleListView1.SelectedItems[0].Index;
            var log = new StringBuilder();
            var newBluRaySubtitles = BluRaySupParser.ParseBluRaySup(openFileDialog1.FileName, log);
            foreach (var s in newBluRaySubtitles)
            {
                _subtitle.Paragraphs.Add(new Paragraph
                {
                    StartTime = new TimeCode(s.StartTime / 90.0),
                    EndTime = new TimeCode(s.EndTime / 90.0)
                });

                var pos = s.GetPosition();
                _extra.Add(new Extra { IsForced = s.IsForced, X = pos.Left, Y = pos.Top });
                _binSubtitles.Add(s);
            }

            _subtitle.Renumber();
            subtitleListView1.Fill(_subtitle);

            if (_subtitle != null)
            {
                subtitleListView1.SelectIndexAndEnsureVisible(_subtitle.GetParagraphOrDefault(idx));
            }
        }

        private void adjustAllTimesForSelectedLinesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (subtitleListView1.SelectedItems.Count < 1)
            {
                return;
            }

            using (var form = new ShowEarlierLater())
            {
                form.Initialize(ShowEarlierOrLater, true);
                form.ShowDialog(this);
            }
        }

        private void contextMenuStripListView_Opening(object sender, CancelEventArgs e)
        {
            adjustAllTimesForSelectedLinesToolStripMenuItem.Visible = subtitleListView1.SelectedItems.Count > 1;
            oCRTextsforOverviewOnlyToolStripMenuItem.Visible = File.Exists(_nOcrFileName);
        }

        private void openVideoToolStripMenuItem_Click(object sender, EventArgs e)
        {
            openFileDialog1.Title = Configuration.Settings.Language.General.OpenVideoFileTitle;
            openFileDialog1.FileName = string.Empty;
            openFileDialog1.Filter = UiUtil.GetVideoFileFilter(true);

            openFileDialog1.FileName = string.Empty;
            if (openFileDialog1.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            if (!videoPlayerContainer1.Visible)
            {
                videoPlayerContainer1.BringToFront();
                pictureBoxMovableImage.BringToFront();
                videoPlayerContainer1.Visible = true;
                videoPlayerContainer1.Dock = DockStyle.Fill;
            }

            if (videoPlayerContainer1.VideoPlayer != null)
            {
                videoPlayerContainer1.Pause();
                videoPlayerContainer1.VideoPlayer.DisposeVideoPlayer();
            }

            var videoInfo = UiUtil.GetVideoInfo(openFileDialog1.FileName);
            _videoFileName = openFileDialog1.FileName;
            UiUtil.InitializeVideoPlayerAndContainer(openFileDialog1.FileName, videoInfo, videoPlayerContainer1, VideoStartLoaded, VideoStartEnded);
            labelVideoInfo.Text = $"{videoInfo.Width}x{videoInfo.Height}, {Path.GetFileName(_videoFileName)}";
            subtitleListView1_SelectedIndexChanged(null, null);
        }

        private void VideoStartEnded(object sender, EventArgs e)
        {
            videoPlayerContainer1.Pause();
            if (videoPlayerContainer1.VideoPlayer is LibMpvDynamic libmpv)
            {
                libmpv.RemoveSubtitle();
            }
        }

        private void VideoStartLoaded(object sender, EventArgs e)
        {
            videoPlayerContainer1.Pause();
            timerSubtitleOnVideo.Start();
        }

        private void timerSubtitleOnVideo_Tick(object sender, EventArgs e)
        {
            if (videoPlayerContainer1 == null || videoPlayerContainer1.IsPaused)
            {
                return;
            }

            double pos;
            if (!videoPlayerContainer1.IsPaused)
            {
                videoPlayerContainer1.RefreshProgressBar();
                pos = videoPlayerContainer1.CurrentPosition;
            }
            else
            {
                pos = videoPlayerContainer1.CurrentPosition;
            }

            var sub = _subtitle.GetFirstParagraphOrDefaultByTime(pos * 1000.0);
            if (sub == null)
            {
                pictureBoxMovableImage.Hide();
            }

            if (sub != null)
            {
                if (_lastPlayParagraph != sub)
                {
                    subtitleListView1.SelectIndexAndEnsureVisible(_subtitle.Paragraphs.IndexOf(sub));
                    _lastPlayParagraph = sub;
                }
                pictureBoxMovableImage.Show();
            }
        }

        private void BinEdit_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_extra != null && HasChanges())
            {
                var result = MessageBox.Show(this, "Close and lose changes?", "SE", MessageBoxButtons.YesNoCancel);
                if (result != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }

            CloseVideo();
        }

        private bool HasChanges()
        {
            return GetStateHash() != _lastSaveHash;
        }

        private void closeVideoToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CloseVideo();
            subtitleListView1_SelectedIndexChanged(null, null);
        }

        private void CloseVideo()
        {
            timerSubtitleOnVideo.Stop();

            Application.DoEvents();
            if (videoPlayerContainer1.VideoPlayer != null)
            {
                videoPlayerContainer1.Pause();
                videoPlayerContainer1.VideoPlayer.DisposeVideoPlayer();
                videoPlayerContainer1.VideoPlayer = null;
            }

            Application.DoEvents();
            _videoFileName = null;
            videoPlayerContainer1.Visible = false;
            labelVideoInfo.Text = string.Empty;
        }

        private void videoToolStripMenuItem_DropDownOpening(object sender, EventArgs e)
        {
            closeVideoToolStripMenuItem.Visible = !string.IsNullOrEmpty(_videoFileName);
        }

        private void buttonSetText_Click(object sender, EventArgs e)
        {
            if (subtitleListView1.SelectedItems.Count < 1)
            {
                return;
            }

            var idx = subtitleListView1.SelectedItems[0].Index;
            using (var form = new BinEditNewText(_subtitle.Paragraphs[idx].Text))
            {
                if (form.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                _extra[idx].Bitmap?.Dispose();
                _extra[idx].Bitmap = (Bitmap)form.Bitmap.Clone();
                subtitleListView1_SelectedIndexChanged(null, null);
            }
        }

        private void undoChangesForThisElementToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (subtitleListView1.SelectedItems.Count < 1)
            {
                return;
            }

            var idx = subtitleListView1.SelectedItems[0].Index;
            if (_binSubtitles != null)
            {
                var s = _binSubtitles[idx];
                var p = _subtitle.Paragraphs[idx];
                p.StartTime = s.StartTimeCode;
                p.EndTime = s.EndTimeCode;
                var pos = s.GetPosition();
                var extra = _extra[idx];
                extra.Bitmap = null;
                extra.IsForced = s.IsForced;
                extra.X = pos.Left;
                extra.Y = pos.Top;
                subtitleListView1.SelectIndexAndEnsureVisible(idx);
            }
        }

        private void comboBoxFrameRate_SelectedValueChanged(object sender, EventArgs e)
        {
            _frameRate = double.Parse(comboBoxFrameRate.SelectedItem.ToString());
        }

        private void comboBoxFrameRate_SelectedIndexChanged(object sender, EventArgs e)
        {
            _frameRate = double.Parse(comboBoxFrameRate.SelectedItem.ToString());
        }

        private void numericUpDownScreenWidth_ValueChanged(object sender, EventArgs e)
        {
            _screenWidth = (int)numericUpDownScreenWidth.Value;
        }

        private void numericUpDownScreenHeight_ValueChanged(object sender, EventArgs e)
        {
            _screenHeight = (int)numericUpDownScreenHeight.Value;
        }

        private void BinEdit_Resize(object sender, EventArgs e)
        {
            if (subtitleListView1.SelectedItems.Count < 1)
            {
                return;
            }

            var idx = subtitleListView1.SelectedItems[0].Index;
            if (_binSubtitles != null)
            {
                var sub = _binSubtitles[idx];
                var extra = _extra[idx];
                var bmp = extra.Bitmap != null ? (Bitmap)extra.Bitmap.Clone() : sub.GetBitmap();
                ShowCurrentScaledImage(bmp, extra);
            }
        }

        private void ShowCurrentScaledImage(Bitmap bmp, Extra extra)
        {
            var controlHeight = 0;
            if (!string.IsNullOrEmpty(_videoFileName))
            {
                controlHeight = videoPlayerContainer1.ControlsHeight;
            }

            var widthAspect = pictureBoxScreen.Width / numericUpDownScreenWidth.Value;
            var heightAspect = (pictureBoxScreen.Height - controlHeight) / numericUpDownScreenHeight.Value;
            var scaledBmp = new Bitmap((int)Math.Round(bmp.Width * widthAspect), (int)Math.Round(bmp.Height * heightAspect));
            using (var g = Graphics.FromImage(scaledBmp))
            {
                using (var brush = new SolidBrush(Color.Black))
                {
                    g.FillRectangle(brush, 0, 0, scaledBmp.Width, scaledBmp.Height);
                }
                g.DrawImage(bmp, 0, 0, scaledBmp.Width, scaledBmp.Height);
            }

            bmp.Dispose();
            pictureBoxMovableImage.Hide();
            var oldImage = pictureBoxMovableImage.Image;
            pictureBoxMovableImage.Width = scaledBmp.Width;
            pictureBoxMovableImage.Height = scaledBmp.Height;
            pictureBoxMovableImage.Image = scaledBmp;
            pictureBoxMovableImage.Left = (int)Math.Round(extra.X * widthAspect);
            pictureBoxMovableImage.Top = (int)Math.Round(extra.Y * heightAspect);
            pictureBoxMovableImage.Invalidate();
            pictureBoxMovableImage.Show();
            oldImage?.Dispose();
        }

        private void subtitleListView1_Click(object sender, EventArgs e)
        {
            if (subtitleListView1.SelectedItems.Count < 1 && string.IsNullOrEmpty(_videoFileName))
            {
                return;
            }

            var idx = subtitleListView1.SelectedItems[0].Index;
            var p = _subtitle.Paragraphs[idx];
            videoPlayerContainer1.CurrentPosition = p.StartTime.TotalSeconds;
        }

        private void ocrTextsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (subtitleListView1.Items.Count < 1)
            {
                return;
            }

            progressBar1.Value = 0;
            progressBar1.Maximum = _subtitle.Paragraphs.Count;
            progressBar1.Visible = true;
            var nOcrDb = new NOcrDb(_nOcrFileName);
            int count = 0;
            var bw = new BackgroundWorker { WorkerReportsProgress = true };
            bw.DoWork += (o, args) =>
            {
                Parallel.For(0, _subtitle.Paragraphs.Count, i =>
                {
                    Interlocked.Increment(ref count);
                    bw.ReportProgress(count);
                    var p = _subtitle.Paragraphs[i];
                    var s = _binSubtitles[i];
                    var extra = _extra[i];
                    OcrParagraph(extra, s, nOcrDb, p);
                });
            };
            bw.ProgressChanged += (o, args) =>
            {
                progressBar1.Value = args.ProgressPercentage;
            };
            bw.RunWorkerCompleted += (o, args) =>
            {
                if (IsDisposed)
                {
                    return;
                }

                progressBar1.Visible = false;
                var idx = 0;
                if (subtitleListView1.SelectedItems.Count > 0)
                {
                    idx = subtitleListView1.SelectedItems[0].Index;
                }
                subtitleListView1.Fill(_subtitle);
                subtitleListView1.SelectIndexAndEnsureVisible(idx);
                _nOcrFileName = null;
            };
            bw.RunWorkerAsync();
        }

        private static void OcrParagraph(Extra extra, IBinaryParagraphWithPosition s, NOcrDb nOcrDb, Paragraph p)
        {
            var bmp = extra.Bitmap != null ? (Bitmap)extra.Bitmap.Clone() : s.GetBitmap();
            var nBmp = new NikseBitmap(bmp);
            nBmp.MakeTwoColor(200);
            var list = NikseBitmapImageSplitter.SplitBitmapToLettersNew(nBmp, 8, false, true, 15, true);
            var sb = new StringBuilder();
            foreach (var item in list)
            {
                if (item.NikseBitmap == null)
                {
                    if (item.SpecialCharacter != null)
                    {
                        sb.Append(item.SpecialCharacter);
                    }
                }
                else
                {
                    var match = nOcrDb.GetMatch(item.NikseBitmap, item.Top, true, 40);
                    sb.Append(match != null ? match.Text : "*");
                }
            }

            p.Text = sb.ToString().Trim();
        }
    }
}
