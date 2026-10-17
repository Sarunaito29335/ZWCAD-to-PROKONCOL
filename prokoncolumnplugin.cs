using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.Runtime;

[assembly: CommandClass(typeof(ProkonZwCadPlugin.ProkonColumnCommands))]

namespace ProkonZwCadPlugin
{
    public class ProkonRow
    {
        public string Code { get; set; }
        public string XOrRadius { get; set; }
        public string YOrAngle { get; set; }
    }

    public class ProkonPolygon
    {
        /// <summary>
        /// "+" for outer solid polygon, "-" for inner opening/void subtraction.
        /// </summary>
        public string PolygonCode { get; set; } = "+";
        public List<Point2d> Vertices { get; set; } = new List<Point2d>();
    }

    public class ProkonRebarSet
    {
        /// <summary>
        /// Bar diameter in mm (e.g. "12", "16", "20", "25").
        /// </summary>
        public string BarDiameter { get; set; } = "12";
        public List<Point2d> Points { get; set; } = new List<Point2d>();
    }

    public class ProkonColumnCommands
    {
        private const double Tolerance = 1e-4;
        private static string _lastBarDiameter = "12";

        /// <summary>
        /// Main ZWCAD command: supports Outer boundaries (+), Inner openings (-),
        /// and Rebar point sets (b) in one unified workflow.
        /// </summary>
        [CommandMethod("PROKONCOL")]
        public void GenerateProkonColumnCoordinates()
        {
            RunProkonWorkflow(startWithRebarOnly: false);
        }

        /// <summary>
        /// Direct shortcut command that jumps straight into selecting Rebar POINTs in a loop.
        /// </summary>
        [CommandMethod("PROKONBAR")]
        public void GenerateProkonRebarCoordinates()
        {
            RunProkonWorkflow(startWithRebarOnly: true);
        }

        private void RunProkonWorkflow(bool startWithRebarOnly)
        {
            Document doc = ZwSoft.ZwCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            Database db = doc.Database;
            Editor ed = doc.Editor;

            try
            {
                List<ProkonPolygon> polygons = new List<ProkonPolygon>();
                List<ProkonRebarSet> rebarSets = new List<ProkonRebarSet>();
                HashSet<ObjectId> usedPointIds = new HashSet<ObjectId>();

                if (startWithRebarOnly)
                {
                    PromptRebarSetsLoop(ed, db, rebarSets, usedPointIds);
                }
                else
                {
                    string nextDefault = "Outer";

                    while (true)
                    {
                        PromptKeywordOptions actionOpts = new PromptKeywordOptions(
                            $"\nSelect element to add [Outer/Inner/Rebar/Done] <{nextDefault}>: ",
                            "Outer Inner Rebar Done");
                        actionOpts.AllowNone = true;
                        PromptResult actionRes = ed.GetKeywords(actionOpts);
                        if (actionRes.Status != PromptStatus.OK && actionRes.Status != PromptStatus.None)
                            break;

                        string action = (actionRes.Status == PromptStatus.None || string.IsNullOrEmpty(actionRes.StringResult))
                            ? nextDefault
                            : actionRes.StringResult;

                        if (action == "Done")
                            break;

                        if (action == "Rebar")
                        {
                            PromptRebarSetsLoop(ed, db, rebarSets, usedPointIds);
                            nextDefault = "Done";
                        }
                        else
                        {
                            string polyCode = (action == "Inner") ? "-" : "+";
                            PromptAddPolygon(ed, db, polyCode, polygons);
                            // After adding an outer section, suggest Rebar or Inner opening next; finish on Enter
                            nextDefault = "Done";
                        }
                    }
                }

                if (polygons.Count == 0 && rebarSets.Count == 0)
                {
                    ed.WriteMessage("\nNo boundaries or rebar points were selected.");
                    return;
                }

                // Show interactive preview & export dialog
                using (ProkonPreviewForm form = new ProkonPreviewForm(polygons, rebarSets, 1.0))
                {
                    DialogResult dlgRes = ZwSoft.ZwCAD.ApplicationServices.Application.ShowModalDialog(form);
                    if (dlgRes == DialogResult.Yes && form.GeneratedRows.Count > 0)
                    {
                        InsertCadTable(doc, db, ed, form.GeneratedRows);
                    }
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nError generating PROKON coordinates: {ex.Message}");
            }
        }

        private void PromptAddPolygon(Editor ed, Database db, string polyCode, List<ProkonPolygon> polygons)
        {
            PromptKeywordOptions modeOpts = new PromptKeywordOptions(
                $"\n[{polyCode}] Select input method [SelectObjects/PickPoints] <SelectObjects>: ",
                "SelectObjects PickPoints");
            modeOpts.AllowNone = true;
            PromptResult modeRes = ed.GetKeywords(modeOpts);
            if (modeRes.Status != PromptStatus.OK && modeRes.Status != PromptStatus.None)
                return;

            string mode = (modeRes.Status == PromptStatus.None || string.IsNullOrEmpty(modeRes.StringResult))
                ? "SelectObjects"
                : modeRes.StringResult;

            List<Point2d> vertices = (mode == "PickPoints")
                ? PromptPickPoints(ed, polyCode)
                : PromptSelectEntities(ed, db, polyCode);

            if (vertices != null && vertices.Count >= 3)
            {
                if (vertices.Count > 1 && vertices[0].GetDistanceTo(vertices[vertices.Count - 1]) < Tolerance)
                {
                    vertices.RemoveAt(vertices.Count - 1);
                }

                if (vertices.Count >= 3)
                {
                    polygons.Add(new ProkonPolygon
                    {
                        PolygonCode = polyCode,
                        Vertices = vertices
                    });
                    ed.WriteMessage($"\nAdded ({polyCode}) boundary with {vertices.Count} vertices.");
                    return;
                }
            }

            ed.WriteMessage("\nNo valid boundary added (at least 3 distinct vertices required).");
        }

        /// <summary>
        /// Loops to let the user select sets of CAD POINT objects.
        /// Filters out only POINT entities, pops up a dialog to enter the rebar diameter (b),
        /// and automatically looks for the next set until the user finishes.
        /// </summary>
        private void PromptRebarSetsLoop(
            Editor ed,
            Database db,
            List<ProkonRebarSet> rebarSets,
            HashSet<ObjectId> usedPointIds)
        {
            // Filter strictly for CAD POINT entities ("POINT") so user can window-select over the whole drawing
            TypedValue[] pointFilterList = new TypedValue[]
            {
                new TypedValue((int)DxfCode.Start, "POINT")
            };
            SelectionFilter pointFilter = new SelectionFilter(pointFilterList);

            Matrix3d wcsToUcs = ed.CurrentUserCoordinateSystem.Inverse();

            while (true)
            {
                int setNum = rebarSets.Count + 1;
                PromptSelectionOptions selOpts = new PromptSelectionOptions
                {
                    MessageForAdding = $"\n[Rebar Set #{setNum}] Select objects (filters POINTs only) or press Enter to finish: "
                };

                PromptSelectionResult selRes = ed.GetSelection(selOpts, pointFilter);
                if (selRes.Status != PromptStatus.OK || selRes.Value == null || selRes.Value.Count == 0)
                {
                    // User pressed Enter or Esc without selecting points -> conclude loop
                    break;
                }

                List<Point2d> currentPoints = new List<Point2d>();
                List<ObjectId> currentIds = new List<ObjectId>();
                int skippedDuplicates = 0;

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in selRes.Value.GetObjectIds())
                    {
                        if (usedPointIds.Contains(id))
                        {
                            skippedDuplicates++;
                            continue;
                        }

                        DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                        if (obj is DBPoint dbPt)
                        {
                            Point3d ptUcs = dbPt.Position.TransformBy(wcsToUcs);
                            currentPoints.Add(new Point2d(ptUcs.X, ptUcs.Y));
                            currentIds.Add(id);
                        }
                    }
                    tr.Commit();
                }

                if (currentPoints.Count == 0)
                {
                    if (skippedDuplicates > 0)
                    {
                        ed.WriteMessage($"\nAll {skippedDuplicates} selected point(s) were already added in an earlier set.");
                        continue;
                    }
                    break;
                }

                // Pop up window to specify the rebar size for this set
                using (RebarSizeDialog sizeDlg = new RebarSizeDialog(setNum, currentPoints.Count, skippedDuplicates, _lastBarDiameter))
                {
                    DialogResult dlgRes = ZwSoft.ZwCAD.ApplicationServices.Application.ShowModalDialog(sizeDlg);
                    if (dlgRes == DialogResult.Cancel)
                    {
                        ed.WriteMessage($"\nSkipped Rebar Set #{setNum}.");
                        continue;
                    }

                    _lastBarDiameter = sizeDlg.BarDiameter;
                    rebarSets.Add(new ProkonRebarSet
                    {
                        BarDiameter = sizeDlg.BarDiameter,
                        Points = currentPoints
                    });

                    foreach (ObjectId id in currentIds)
                    {
                        usedPointIds.Add(id);
                    }

                    ed.WriteMessage($"\nAdded Rebar Set #{rebarSets.Count}: {currentPoints.Count} bar(s) of size b = {sizeDlg.BarDiameter} mm.");

                    // DialogResult.OK = "Add & Select Next Set", DialogResult.Yes = "Add & Finish"
                    if (dlgRes == DialogResult.Yes)
                    {
                        break;
                    }
                }
            }
        }

        private List<Point2d> PromptPickPoints(Editor ed, string polyCode)
        {
            List<Point2d> pts = new List<Point2d>();
            PromptPointOptions ppo = new PromptPointOptions($"\n[{polyCode}] Click first corner of boundary: ");
            PromptPointResult ppr = ed.GetPoint(ppo);
            if (ppr.Status != PromptStatus.OK) return pts;

            Point3d firstPt = ppr.Value;
            Point3d lastPt = firstPt;
            pts.Add(new Point2d(firstPt.X, firstPt.Y));

            while (true)
            {
                PromptPointOptions nextOpts = new PromptPointOptions(
                    $"\n[{polyCode}] Click next corner (or press Enter when finished): ")
                {
                    UseBasePoint = true,
                    BasePoint = lastPt,
                    AllowNone = true
                };
                PromptPointResult nextRes = ed.GetPoint(nextOpts);
                if (nextRes.Status == PromptStatus.None) break;
                if (nextRes.Status != PromptStatus.OK) return null;

                lastPt = nextRes.Value;
                pts.Add(new Point2d(lastPt.X, lastPt.Y));
            }

            return pts;
        }

        private List<Point2d> PromptSelectEntities(Editor ed, Database db, string polyCode)
        {
            TypedValue[] filterList = new TypedValue[]
            {
                new TypedValue((int)DxfCode.Operator, "<OR"),
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE"),
                new TypedValue((int)DxfCode.Start, "POLYLINE"),
                new TypedValue((int)DxfCode.Start, "LINE"),
                new TypedValue((int)DxfCode.Operator, "OR>")
            };
            SelectionFilter filter = new SelectionFilter(filterList);

            PromptSelectionOptions selOpts = new PromptSelectionOptions
            {
                MessageForAdding = $"\n[{polyCode}] Select a Polyline or joined Lines for boundary: "
            };

            PromptSelectionResult selRes = ed.GetSelection(selOpts, filter);
            if (selRes.Status != PromptStatus.OK) return null;

            Matrix3d wcsToUcs = ed.CurrentUserCoordinateSystem.Inverse();
            List<Point2d> vertices = new List<Point2d>();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId[] ids = selRes.Value.GetObjectIds();

                if (ids.Length == 1)
                {
                    DBObject obj = tr.GetObject(ids[0], OpenMode.ForRead);
                    if (obj is Polyline lwPoly)
                    {
                        for (int i = 0; i < lwPoly.NumberOfVertices; i++)
                        {
                            Point3d ptWcs = lwPoly.GetPoint3dAt(i);
                            Point3d ptUcs = ptWcs.TransformBy(wcsToUcs);
                            vertices.Add(new Point2d(ptUcs.X, ptUcs.Y));
                        }
                        tr.Commit();
                        return vertices;
                    }
                    else if (obj is Polyline2d poly2d)
                    {
                        foreach (ObjectId vId in poly2d)
                        {
                            Vertex2d v = tr.GetObject(vId, OpenMode.ForRead) as Vertex2d;
                            if (v != null && v.VertexType != Vertex2dType.CurveFitTangent)
                            {
                                Point3d ptWcs = poly2d.VertexPosition(v);
                                Point3d ptUcs = ptWcs.TransformBy(wcsToUcs);
                                vertices.Add(new Point2d(ptUcs.X, ptUcs.Y));
                            }
                        }
                        tr.Commit();
                        return vertices;
                    }
                }

                List<Tuple<Point2d, Point2d>> segments = new List<Tuple<Point2d, Point2d>>();
                foreach (ObjectId id in ids)
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                    if (obj is Line line)
                    {
                        Point3d sUcs = line.StartPoint.TransformBy(wcsToUcs);
                        Point3d eUcs = line.EndPoint.TransformBy(wcsToUcs);
                        segments.Add(Tuple.Create(
                            new Point2d(sUcs.X, sUcs.Y),
                            new Point2d(eUcs.X, eUcs.Y)));
                    }
                    else if (obj is Polyline poly)
                    {
                        for (int i = 0; i < poly.NumberOfVertices - 1; i++)
                        {
                            Point3d p1 = poly.GetPoint3dAt(i).TransformBy(wcsToUcs);
                            Point3d p2 = poly.GetPoint3dAt(i + 1).TransformBy(wcsToUcs);
                            segments.Add(Tuple.Create(new Point2d(p1.X, p1.Y), new Point2d(p2.X, p2.Y)));
                        }
                        if (poly.Closed && poly.NumberOfVertices > 2)
                        {
                            Point3d pLast = poly.GetPoint3dAt(poly.NumberOfVertices - 1).TransformBy(wcsToUcs);
                            Point3d pFirst = poly.GetPoint3dAt(0).TransformBy(wcsToUcs);
                            segments.Add(Tuple.Create(new Point2d(pLast.X, pLast.Y), new Point2d(pFirst.X, pFirst.Y)));
                        }
                    }
                }

                tr.Commit();
                vertices = ChainSegments(segments);
            }

            return vertices;
        }

        private List<Point2d> ChainSegments(List<Tuple<Point2d, Point2d>> segments)
        {
            List<Point2d> ordered = new List<Point2d>();
            if (segments == null || segments.Count == 0) return ordered;

            List<Tuple<Point2d, Point2d>> remaining = new List<Tuple<Point2d, Point2d>>(segments);
            var first = remaining[0];
            remaining.RemoveAt(0);

            ordered.Add(first.Item1);
            ordered.Add(first.Item2);

            while (remaining.Count > 0)
            {
                Point2d currentEnd = ordered[ordered.Count - 1];
                int matchIndex = -1;
                bool reverse = false;

                for (int i = 0; i < remaining.Count; i++)
                {
                    if (remaining[i].Item1.GetDistanceTo(currentEnd) < Tolerance)
                    {
                        matchIndex = i;
                        reverse = false;
                        break;
                    }
                    if (remaining[i].Item2.GetDistanceTo(currentEnd) < Tolerance)
                    {
                        matchIndex = i;
                        reverse = true;
                        break;
                    }
                }

                if (matchIndex == -1) break;

                var seg = remaining[matchIndex];
                remaining.RemoveAt(matchIndex);
                ordered.Add(reverse ? seg.Item1 : seg.Item2);
            }

            return ordered;
        }

        private void InsertCadTable(Document doc, Database db, Editor ed, List<ProkonRow> rows)
        {
            PromptPointOptions ppo = new PromptPointOptions("\nSpecify insertion point for the coordinate table: ");
            PromptPointResult ppr = ed.GetPoint(ppo);
            if (ppr.Status != PromptStatus.OK) return;

            Matrix3d ucsToWcs = ed.CurrentUserCoordinateSystem;
            Point3d insPtWcs = ppr.Value.TransformBy(ucsToWcs);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                Table tb = new Table();
                tb.TableStyle = db.Tablestyle;
                tb.Position = insPtWcs;

                int totalRows = rows.Count + 2;
                tb.SetSize(totalRows, 3);
                tb.SetRowHeight(8.0);
                tb.SetColumnWidth(35.0);

                tb.Cells[0, 0].TextString = "COLUMN SECTION (PROKON)";
                tb.Cells[1, 0].TextString = "Code";
                tb.Cells[1, 1].TextString = "X/Radius or Bar Diameter (mm)";
                tb.Cells[1, 2].TextString = "Y (mm) Angle °";

                for (int i = 0; i < rows.Count; i++)
                {
                    tb.Cells[i + 2, 0].TextString = rows[i].Code ?? "";
                    tb.Cells[i + 2, 1].TextString = rows[i].XOrRadius ?? "";
                    tb.Cells[i + 2, 2].TextString = rows[i].YOrAngle ?? "";
                }

                tb.GenerateLayout();
                btr.AppendEntity(tb);
                tr.AddNewlyCreatedDBObject(tb, true);
                tr.Commit();
            }

            ed.WriteMessage("\nPROKON coordinate table inserted into drawing.");
        }
    }

    /// <summary>
    /// Popup dialog shown immediately after selecting a group of rebar POINTs.
    /// Pressing Enter confirms the rebar diameter and automatically starts selecting the next set.
    /// </summary>
    public class RebarSizeDialog : Form
    {
        private TextBox _txtSize;
        public string BarDiameter { get; private set; }

        public RebarSizeDialog(int setNumber, int pointCount, int skippedDuplicates, string defaultSize)
        {
            BarDiameter = defaultSize;
            InitializeComponents(setNumber, pointCount, skippedDuplicates, defaultSize);
        }

        private void InitializeComponents(int setNumber, int pointCount, int skippedDuplicates, string defaultSize)
        {
            Text = $"Rebar Set #{setNumber} — Specify Bar Size";
            Width = 420;
            Height = 250;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            string infoText = $"Selected {pointCount} point coordinate(s) for Rebar Set #{setNumber}.";
            if (skippedDuplicates > 0)
            {
                infoText += $" ({skippedDuplicates} already-used point(s) skipped)";
            }

            Label lblInfo = new Label
            {
                Text = infoText,
                Left = 16,
                Top = 14,
                Width = 380,
                Height = 22
            };

            Label lblSize = new Label
            {
                Text = "Rebar Diameter 'b' (mm):",
                Left = 16,
                Top = 48,
                Width = 155,
                Height = 22
            };

            _txtSize = new TextBox
            {
                Text = defaultSize,
                Left = 175,
                Top = 45,
                Width = 100
            };

            Label lblQuick = new Label
            {
                Text = "Quick pick (mm):",
                Left = 16,
                Top = 85,
                Width = 110,
                Height = 20
            };

            string[] standardSizes = new[] { "10", "12", "16", "20", "25", "28", "32" };
            int btnLeft = 16;
            foreach (string sz in standardSizes)
            {
                string capturedSize = sz;
                Button btnPreset = new Button
                {
                    Text = capturedSize,
                    Left = btnLeft,
                    Top = 108,
                    Width = 48,
                    Height = 28
                };
                btnPreset.Click += (s, e) =>
                {
                    _txtSize.Text = capturedSize;
                    _txtSize.Focus();
                    _txtSize.SelectAll();
                };
                Controls.Add(btnPreset);
                btnLeft += 53;
            }

            Button btnNextSet = new Button
            {
                Text = "Add && Next Set (Enter)",
                Left = 16,
                Top = 155,
                Width = 160,
                Height = 38
            };
            btnNextSet.Click += (s, e) =>
            {
                if (!ValidateAndStoreSize()) return;
                DialogResult = DialogResult.OK;
                Close();
            };

            Button btnFinish = new Button
            {
                Text = "Add && Finish to Table",
                Left = 184,
                Top = 155,
                Width = 135,
                Height = 38
            };
            btnFinish.Click += (s, e) =>
            {
                if (!ValidateAndStoreSize()) return;
                DialogResult = DialogResult.Yes;
                Close();
            };

            Button btnCancel = new Button
            {
                Text = "Skip",
                Left = 327,
                Top = 155,
                Width = 65,
                Height = 38
            };
            btnCancel.Click += (s, e) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };

            AcceptButton = btnNextSet;
            CancelButton = btnCancel;

            Controls.AddRange(new Control[]
            {
                lblInfo, lblSize, _txtSize, lblQuick, btnNextSet, btnFinish, btnCancel
            });

            Shown += (s, e) =>
            {
                _txtSize.Focus();
                _txtSize.SelectAll();
            };
        }

        private bool ValidateAndStoreSize()
        {
            string raw = (_txtSize.Text ?? "").Trim();
            if (raw.StartsWith("b", StringComparison.OrdinalIgnoreCase))
            {
                raw = raw.Substring(1).Trim();
            }

            if (string.IsNullOrEmpty(raw) || !double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out double val) || val <= 0)
            {
                MessageBox.Show("Please enter a valid positive rebar diameter (e.g., 12, 16, 20, 25).", "Invalid Bar Size", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtSize.Focus();
                return false;
            }

            BarDiameter = val.ToString("0.###", CultureInfo.InvariantCulture);
            return true;
        }
    }

    /// <summary>
    /// Preview & Clipboard export dialog matching PROKON GenCol's COLUMN SECTION grid.
    /// Outputs boundaries (+ / -) and 2-line Rebar loops ('+' X Y followed by 'b' Size).
    /// </summary>
    public class ProkonPreviewForm : Form
    {
        private readonly List<ProkonPolygon> _polygons;
        private readonly List<ProkonRebarSet> _rebarSets;
        private double _scale;

        private DataGridView _grid;
        private RadioButton _rbAbsoluteCoord;
        private RadioButton _rbRelativeFromTrueStart;
        private CheckBox _chkBlankZeros;
        private CheckBox _chkIncludeClosingVertex;
        private NumericUpDown _numDecimals;
        private TextBox _txtScale;
        private ListBox _lstPolygons;
        private ListBox _lstRebarSets;
        private TextBox _txtEditBarSize;

        public List<ProkonRow> GeneratedRows { get; private set; } = new List<ProkonRow>();

        public ProkonPreviewForm(List<ProkonPolygon> polygons, List<ProkonRebarSet> rebarSets, double initialScale)
        {
            _polygons = polygons ?? new List<ProkonPolygon>();
            _rebarSets = rebarSets ?? new List<ProkonRebarSet>();
            _scale = initialScale;

            InitializeComponents();
            RefreshLists();
            RebuildTable();
        }

        private void InitializeComponents()
        {
            Text = "PROKON Column Section & Rebar Coordinates (GenCol)";
            Width = 650;
            Height = 680;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;

            // 1. Boundaries Group
            GroupBox grpPolys = new GroupBox
            {
                Text = "Section Boundaries ('+' Outer / '-' Inner Opening)",
                Left = 12,
                Top = 10,
                Width = 610,
                Height = 82
            };

            _lstPolygons = new ListBox
            {
                Left = 12,
                Top = 18,
                Width = 435,
                Height = 52
            };

            Button btnTogglePlusMinus = new Button
            {
                Text = "Toggle '+' / '-'",
                Left = 458,
                Top = 18,
                Width = 140,
                Height = 25
            };
            btnTogglePlusMinus.Click += (s, e) =>
            {
                int idx = _lstPolygons.SelectedIndex;
                if (idx >= 0 && idx < _polygons.Count)
                {
                    _polygons[idx].PolygonCode = (_polygons[idx].PolygonCode == "+") ? "-" : "+";
                    RefreshLists();
                    _lstPolygons.SelectedIndex = idx;
                    RebuildTable();
                }
            };

            Button btnRemovePoly = new Button
            {
                Text = "Remove Boundary",
                Left = 458,
                Top = 46,
                Width = 140,
                Height = 25
            };
            btnRemovePoly.Click += (s, e) =>
            {
                int idx = _lstPolygons.SelectedIndex;
                if (idx >= 0 && idx < _polygons.Count)
                {
                    _polygons.RemoveAt(idx);
                    RefreshLists();
                    RebuildTable();
                }
            };

            grpPolys.Controls.AddRange(new Control[] { _lstPolygons, btnTogglePlusMinus, btnRemovePoly });

            // 2. Rebar Sets Group
            GroupBox grpRebar = new GroupBox
            {
                Text = "Rebar Point Sets (Row 1: '+' X Y  ->  Row 2: 'b' Size)",
                Left = 12,
                Top = 98,
                Width = 610,
                Height = 86
            };

            _lstRebarSets = new ListBox
            {
                Left = 12,
                Top = 18,
                Width = 350,
                Height = 56
            };
            _lstRebarSets.SelectedIndexChanged += (s, e) =>
            {
                int idx = _lstRebarSets.SelectedIndex;
                if (idx >= 0 && idx < _rebarSets.Count)
                {
                    _txtEditBarSize.Text = _rebarSets[idx].BarDiameter;
                }
            };

            Label lblBarEdit = new Label
            {
                Text = "Size (b):",
                Left = 370,
                Top = 22,
                Width = 52,
                Height = 20
            };

            _txtEditBarSize = new TextBox
            {
                Left = 424,
                Top = 19,
                Width = 55,
                Text = "12"
            };

            Button btnUpdateBarSize = new Button
            {
                Text = "Update Size",
                Left = 486,
                Top = 18,
                Width = 112,
                Height = 25
            };
            btnUpdateBarSize.Click += (s, e) =>
            {
                int idx = _lstRebarSets.SelectedIndex;
                if (idx >= 0 && idx < _rebarSets.Count)
                {
                    string raw = (_txtEditBarSize.Text ?? "").Trim();
                    if (raw.StartsWith("b", StringComparison.OrdinalIgnoreCase))
                        raw = raw.Substring(1).Trim();

                    if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out double val) && val > 0)
                    {
                        _rebarSets[idx].BarDiameter = val.ToString("0.###", CultureInfo.InvariantCulture);
                        RefreshLists();
                        _lstRebarSets.SelectedIndex = idx;
                        RebuildTable();
                    }
                }
            };

            Button btnRemoveRebarSet = new Button
            {
                Text = "Remove Rebar Set",
                Left = 458,
                Top = 48,
                Width = 140,
                Height = 25
            };
            btnRemoveRebarSet.Click += (s, e) =>
            {
                int idx = _lstRebarSets.SelectedIndex;
                if (idx >= 0 && idx < _rebarSets.Count)
                {
                    _rebarSets.RemoveAt(idx);
                    RefreshLists();
                    RebuildTable();
                }
            };

            grpRebar.Controls.AddRange(new Control[]
            {
                _lstRebarSets, lblBarEdit, _txtEditBarSize, btnUpdateBarSize, btnRemoveRebarSet
            });

            // 3. Coordinate Format & Unit Scale Options
            GroupBox grpOptions = new GroupBox
            {
                Text = "PROKON Coordinate Options (Measured from ZWCAD 0,0)",
                Left = 12,
                Top = 190,
                Width = 610,
                Height = 98
            };

            _rbAbsoluteCoord = new RadioButton
            {
                Text = "Coordinate Mode (Code '+' / '-' start, Code 'L' for exact X, Y from 0,0)",
                Left = 15,
                Top = 20,
                Width = 580,
                Checked = true
            };
            _rbAbsoluteCoord.CheckedChanged += (s, e) => RebuildTable();

            _rbRelativeFromTrueStart = new RadioButton
            {
                Text = "Relative Mode from true start coordinate (Code '+' / '-' at X0, Y0, then dX, dY)",
                Left = 15,
                Top = 43,
                Width = 580,
                Checked = false
            };
            _rbRelativeFromTrueStart.CheckedChanged += (s, e) => RebuildTable();

            _chkBlankZeros = new CheckBox
            {
                Text = "Leave 0 blank (polygons)",
                Left = 15,
                Top = 68,
                Width = 155,
                Checked = false
            };
            _chkBlankZeros.CheckedChanged += (s, e) => RebuildTable();

            _chkIncludeClosingVertex = new CheckBox
            {
                Text = "Close polygon row",
                Left = 175,
                Top = 68,
                Width = 130,
                Checked = false
            };
            _chkIncludeClosingVertex.CheckedChanged += (s, e) => RebuildTable();

            Label lblScale = new Label
            {
                Text = "Scale to mm:",
                Left = 310,
                Top = 70,
                Width = 75
            };

            _txtScale = new TextBox
            {
                Text = _scale.ToString("0.###", CultureInfo.InvariantCulture),
                Left = 388,
                Top = 67,
                Width = 55
            };
            _txtScale.TextChanged += (s, e) =>
            {
                if (double.TryParse(_txtScale.Text.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double sc) && sc > 0)
                {
                    _scale = sc;
                    RebuildTable();
                }
            };

            Label lblDec = new Label
            {
                Text = "Decimals:",
                Left = 460,
                Top = 70,
                Width = 60
            };

            _numDecimals = new NumericUpDown
            {
                Left = 525,
                Top = 67,
                Width = 55,
                Minimum = 0,
                Maximum = 4,
                Value = 3
            };
            _numDecimals.ValueChanged += (s, e) => RebuildTable();

            grpOptions.Controls.AddRange(new Control[]
            {
                _rbAbsoluteCoord, _rbRelativeFromTrueStart,
                _chkBlankZeros, _chkIncludeClosingVertex,
                lblScale, _txtScale, lblDec, _numDecimals
            });

            // 4. DataGridView
            _grid = new DataGridView
            {
                Left = 12,
                Top = 295,
                Width = 610,
                Height = 285,
                AllowUserToAddRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            _grid.Columns.Add("Code", "Code");
            _grid.Columns.Add("X", "X/Radius or Bar Diameter (mm)");
            _grid.Columns.Add("Y", "Y (mm) Angle °");
            _grid.Columns[0].FillWeight = 18;
            _grid.Columns[1].FillWeight = 44;
            _grid.Columns[2].FillWeight = 38;

            // 5. Action Buttons
            Button btnCopy = new Button
            {
                Text = "Copy for PROKON (Ctrl+V)",
                Left = 12,
                Top = 590,
                Width = 220,
                Height = 40
            };
            btnCopy.Click += (s, e) =>
            {
                CopyToClipboard();
                MessageBox.Show(
                    "Coordinates and rebar rows copied to clipboard!\n\nIn PROKON (General Column Design), click the cell under 'Code' in the COLUMN SECTION table and press Ctrl+V.",
                    "Copied to Clipboard",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            };

            Button btnInsertTable = new Button
            {
                Text = "Copy && Insert CAD Table",
                Left = 242,
                Top = 590,
                Width = 200,
                Height = 40
            };
            btnInsertTable.Click += (s, e) =>
            {
                CopyToClipboard();
                DialogResult = DialogResult.Yes;
                Close();
            };

            Button btnClose = new Button
            {
                Text = "Close",
                Left = 452,
                Top = 590,
                Width = 170,
                Height = 40
            };
            btnClose.Click += (s, e) =>
            {
                DialogResult = DialogResult.OK;
                Close();
            };

            Controls.AddRange(new Control[]
            {
                grpPolys, grpRebar, grpOptions, _grid, btnCopy, btnInsertTable, btnClose
            });
        }

        private void RefreshLists()
        {
            _lstPolygons.Items.Clear();
            for (int i = 0; i < _polygons.Count; i++)
            {
                var p = _polygons[i];
                string typeDesc = (p.PolygonCode == "-") ? "Inner Opening (-)" : "Outer Section (+)";
                Point2d first = p.Vertices[0];
                _lstPolygons.Items.Add(
                    $"Boundary #{i + 1}: {typeDesc} | {p.Vertices.Count} vertices | Start=({first.X * _scale:0.###}, {first.Y * _scale:0.###})");
            }
            if (_lstPolygons.Items.Count > 0 && _lstPolygons.SelectedIndex < 0)
            {
                _lstPolygons.SelectedIndex = 0;
            }

            _lstRebarSets.Items.Clear();
            for (int i = 0; i < _rebarSets.Count; i++)
            {
                var r = _rebarSets[i];
                _lstRebarSets.Items.Add(
                    $"Rebar Set #{i + 1}: {r.Points.Count} bar(s) | Size b = {r.BarDiameter} mm");
            }
            if (_lstRebarSets.Items.Count > 0 && _lstRebarSets.SelectedIndex < 0)
            {
                _lstRebarSets.SelectedIndex = 0;
            }
        }

        private void RebuildTable()
        {
            GeneratedRows.Clear();
            _grid.Rows.Clear();

            int decimals = (int)_numDecimals.Value;
            string fmt = decimals == 0 ? "0" : "0." + new string('0', decimals);

            // 1. Generate Polygon Boundaries (+ and -)
            foreach (var poly in _polygons)
            {
                List<Point2d> verts = poly.Vertices;
                if (verts == null || verts.Count < 3) continue;

                if (_rbAbsoluteCoord.Checked)
                {
                    for (int i = 0; i < verts.Count; i++)
                    {
                        double x = verts[i].X * _scale;
                        double y = verts[i].Y * _scale;

                        GeneratedRows.Add(new ProkonRow
                        {
                            Code = (i == 0) ? poly.PolygonCode : "L",
                            XOrRadius = FormatNum(x, fmt, _chkBlankZeros.Checked),
                            YOrAngle = FormatNum(y, fmt, _chkBlankZeros.Checked)
                        });
                    }

                    if (_chkIncludeClosingVertex.Checked)
                    {
                        GeneratedRows.Add(new ProkonRow
                        {
                            Code = "L",
                            XOrRadius = FormatNum(verts[0].X * _scale, fmt, _chkBlankZeros.Checked),
                            YOrAngle = FormatNum(verts[0].Y * _scale, fmt, _chkBlankZeros.Checked)
                        });
                    }
                }
                else
                {
                    Point2d startPt = verts[0];
                    GeneratedRows.Add(new ProkonRow
                    {
                        Code = poly.PolygonCode,
                        XOrRadius = FormatNum(startPt.X * _scale, fmt, _chkBlankZeros.Checked),
                        YOrAngle = FormatNum(startPt.Y * _scale, fmt, _chkBlankZeros.Checked)
                    });

                    int edgeCount = _chkIncludeClosingVertex.Checked ? verts.Count : verts.Count - 1;
                    for (int i = 0; i < edgeCount; i++)
                    {
                        Point2d curr = verts[i];
                        Point2d next = verts[(i + 1) % verts.Count];

                        double dx = (next.X - curr.X) * _scale;
                        double dy = (next.Y - curr.Y) * _scale;

                        GeneratedRows.Add(new ProkonRow
                        {
                            Code = "",
                            XOrRadius = FormatNum(dx, fmt, _chkBlankZeros.Checked),
                            YOrAngle = FormatNum(dy, fmt, _chkBlankZeros.Checked)
                        });
                    }
                }
            }

            // 2. Generate Rebar Point Loops:
            // Line 1: Code = "+", X = Point X, Y = Point Y
            // Line 2: Code = "b", X = BarDiameter, Y = ""
            foreach (var rebarSet in _rebarSets)
            {
                if (rebarSet.Points == null) continue;

                foreach (Point2d pt in rebarSet.Points)
                {
                    double rx = pt.X * _scale;
                    double ry = pt.Y * _scale;

                    // First line of loop: '+' with exact point coordinates
                    GeneratedRows.Add(new ProkonRow
                    {
                        Code = "+",
                        XOrRadius = rx.ToString(fmt, CultureInfo.InvariantCulture),
                        YOrAngle = ry.ToString(fmt, CultureInfo.InvariantCulture)
                    });

                    // Second line of loop: 'b' with bar diameter in X column
                    GeneratedRows.Add(new ProkonRow
                    {
                        Code = "b",
                        XOrRadius = rebarSet.BarDiameter,
                        YOrAngle = ""
                    });
                }
            }

            foreach (var r in GeneratedRows)
            {
                _grid.Rows.Add(r.Code, r.XOrRadius, r.YOrAngle);
            }
        }

        private string FormatNum(double val, string fmt, bool blankIfZero)
        {
            if (Math.Abs(val) < 1e-6)
            {
                return blankIfZero ? "" : (0.0).ToString(fmt, CultureInfo.InvariantCulture);
            }
            return val.ToString(fmt, CultureInfo.InvariantCulture);
        }

        private void CopyToClipboard()
        {
            StringBuilder sb = new StringBuilder();
            foreach (var r in GeneratedRows)
            {
                sb.Append(r.Code ?? "")
                  .Append('\t')
                  .Append(r.XOrRadius ?? "")
                  .Append('\t')
                  .Append(r.YOrAngle ?? "")
                  .Append("\r\n");
            }
            Clipboard.SetText(sb.ToString());
        }
    }
}