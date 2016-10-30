using System;
using System.Collections.Generic;
using System.Windows;
using Profiler.Data;
using System.Windows.Media;
using Profiler.DirectX;
using System.Windows.Forms;

namespace Profiler
{
    public class EventsThreadRow : ThreadRow
    {
        ThreadDescription Description { get; set; }
        ThreadData EventData { get; set; }
        int MaxDepth { get; set; }

		Mesh Mesh { get; set; }
		Mesh SyncMesh { get; set; }
		DynamicMesh CallstackMeshPolys { get; set; }
        DynamicMesh CallstackMeshLines { get; set; }

        double SyncLineHeight = 4.0 * RenderSettings.dpiScaleY;
        static Color SynchronizationColor = Colors.OrangeRed;
        static Color SynchronizationColorUser = Colors.Yellow;

        double CallstackMarkerSize = 8.0 * RenderSettings.dpiScaleY;
        double CallstackMarkerOffset = -1.5 * RenderSettings.dpiScaleY;

        struct IntPair
        {
            public int count;
            public long duration;
        }


        bool IsUserInitiatedSync(SyncReason reason)
        {
            return false;
        }

		static Color CallstackColor = Colors.Red;

        EventFilter Filter { get; set; }
        Mesh FilterMesh;

        public EventsThreadRow(FrameGroup group, ThreadDescription desc, ThreadData data)
        {
            Description = desc;
            EventData = data;
            Group = group;

			List<EventNode> rootCategories = new List<EventNode>();
			List<EventNode> nodesToProcess = new List<EventNode>();

			foreach (EventFrame frame in data.Events)
			{
				// Fill holes in timeline from regular events (not categories)
				// ------------------------------------------------------------------------------------------------------
				const double thresholdMs = 0.1;

				EventTree categoriesTree = frame.CategoriesTree;
				rootCategories.Clear();
				foreach (EventNode node in frame.CategoriesTree.Children)
				{
					rootCategories.Add(node);
				}

				if (rootCategories.Count != 0)
				{
					nodesToProcess.Clear();
					foreach (EventNode node in frame.Root.Children)
					{
						nodesToProcess.Add(node);
					}

					while(nodesToProcess.Count > 0)
					{
						EventNode node = nodesToProcess[0];
						nodesToProcess.RemoveAt(0);

						bool nodeIntersectWithCategories = false;

						foreach(EventNode categoryNode in rootCategories)
						{
							// drop nodes less than thresholdMs ms
							if (node.Entry.Duration < thresholdMs)
							{
								nodeIntersectWithCategories = true;
								break;
							}

							// node is entirely inside the categoryNode
							if (node.Entry.Start >= categoryNode.Entry.Start && node.Entry.Finish <= categoryNode.Entry.Finish)
							{
								nodeIntersectWithCategories = true;
								break;
							}

							// node is partially inside the categoryNode
							if (node.Entry.Intersect(categoryNode.Entry))
							{
								foreach (EventNode tmp in node.Children)
								{
									nodesToProcess.Add(tmp);
								}

								nodeIntersectWithCategories = true;
								break;
							}
						}

						if (nodeIntersectWithCategories == false && node.Entry.Duration >= thresholdMs)
						{
							// node is not intersect with any categoryNode (add to category tree)
							EventNode fakeCategoryNode = new EventNode(frame.CategoriesTree, node.Entry);

							node.Entry.SetOverrideColor( GenerateColorFromString(node.Entry.Description.FullName) );
							
							rootCategories.Add(fakeCategoryNode);
							frame.CategoriesTree.Children.Add(fakeCategoryNode);
						}
					}
				}
				// ------------------------------------------------------------------------------------------------------

				MaxDepth = Math.Max(frame.CategoriesTree.Depth, MaxDepth);
			}
        }

        void BuildMeshNode(DirectX.DynamicMesh builder, ThreadScroll scroll, EventNode node, int level)
        {
            if (level == MaxDepth)
                return;

            Interval interval = scroll.TimeToUnit(node.Entry);

            double y = (double)level / MaxDepth;
            double h = 1.0 / MaxDepth;

            builder.AddRect(new Rect(interval.Left, y, interval.Width, h), node.Description.Color);

            foreach (EventNode child in node.Children)
            {
                BuildMeshNode(builder, scroll, child, level + 1);
            }
        }

        public override void BuildMesh(DirectX.DirectXCanvas canvas, ThreadScroll scroll)
        {
            // Build Mesh
            DirectX.DynamicMesh builder = canvas.CreateMesh();
            DirectX.DynamicMesh syncBuilder = canvas.CreateMesh();

            foreach (EventFrame frame in EventData.Events)
            {
                Durable interval = Group.Board.TimeSlice;

                foreach (EventNode node in frame.CategoriesTree.Children)
                {
                    BuildMeshNode(builder, scroll, node, 0);
                }

                Interval frameInterval = scroll.TimeToUnit(frame.Header);

                Color color = SynchronizationColor;
                double start = frameInterval.Left;

                if (frame.Synchronization != null)
                {
                    foreach (SyncInterval sync in frame.Synchronization)
                    {
                        Interval syncInterval = scroll.TimeToUnit(sync);

                        if (start < syncInterval.Left)
                        {
                            syncBuilder.AddRect(new Rect(start, RenderParams.BaseMargin / Height, syncInterval.Left - start, SyncLineHeight / Height), color);
                        }

                        start = Math.Max(syncInterval.Right, start);
                        color = IsUserInitiatedSync(sync.Reason) ? SynchronizationColorUser : SynchronizationColor;
                    }
                }
            }

            Mesh = builder.Freeze(canvas.RenderDevice);
            SyncMesh = syncBuilder.Freeze(canvas.RenderDevice);

			CallstackMeshPolys = canvas.CreateMesh();

            CallstackMeshLines = canvas.CreateMesh();
            CallstackMeshLines.Geometry = Mesh.GeometryType.Lines;
        }

        public override double Height { get { return RenderParams.BaseHeight * MaxDepth; } }
        public override string Name { get { return Description.Name; } }

        double TextDrawThreshold = 8.0 * RenderSettings.dpiScaleX;
        double TextDrawOffset = 1.5 * RenderSettings.dpiScaleY;

        public override void Render(DirectX.DirectXCanvas canvas, ThreadScroll scroll)
        {
            SharpDX.Matrix world = SharpDX.Matrix.Scaling((float)scroll.Zoom, (float)((Height - 2.0 * RenderParams.BaseMargin) / scroll.Height), 1.0f);
            world.TranslationVector = new SharpDX.Vector3(-(float)(scroll.ViewUnit.Left * scroll.Zoom), (float)((Offset + 1.0 * RenderParams.BaseMargin) / scroll.Height), 0.0f);

            if (Mesh != null)
            {
                Mesh.World = world;
                canvas.Draw(Mesh);
            }

            if (FilterMesh != null)
            {
                FilterMesh.World = world;
                canvas.Draw(FilterMesh);
            }

            if (SyncMesh != null)
            {
                SyncMesh.World = world;
                canvas.Draw(SyncMesh);
            }

            if (CallstackMeshPolys != null && CallstackMeshLines != null && scroll.DrawCallstacks)
            {
                double unitWidth = scroll.PixelToUnitLength(CallstackMarkerSize * 0.5);
                double unitHeight = (CallstackMarkerSize / RenderParams.BaseHeight) / MaxDepth;
                double offset = (CallstackMarkerOffset / RenderParams.BaseHeight) / MaxDepth;

                Data.Utils.ForEachInsideInterval(EventData.Callstacks, scroll.ViewTime, callstack =>
                {
                    double center = scroll.TimeToUnit(callstack);

                    Point a = new Point(center - unitWidth, offset);
                    Point b = new Point(center, offset + unitHeight);
                    Point c = new Point(center + unitWidth, offset);

                    CallstackMeshPolys.AddTri(a, b, c, CallstackColor);
                    CallstackMeshLines.AddTri(a, b, c, Colors.Black);
                });

                CallstackMeshPolys.Update(canvas.RenderDevice);
                CallstackMeshLines.Update(canvas.RenderDevice);

                CallstackMeshPolys.World = world;
                CallstackMeshLines.World = world;

                canvas.DrawLater(CallstackMeshPolys);
                canvas.DrawLater(CallstackMeshLines);
            }

            Data.Utils.ForEachInsideInterval(EventData.Events, scroll.ViewTime, frame =>
            {
                frame.CategoriesTree.ForEachChild((node, level) =>
                {
                    Entry entry = (node as EventNode).Entry;
                    Interval intervalPx = scroll.TimeToPixel(entry);

                    if (intervalPx.Width < TextDrawThreshold || intervalPx.Right < 0.0)
                        return false;

                    if (intervalPx.Left < 0.0)
                    {
                        intervalPx.Width += intervalPx.Left;
                        intervalPx.Left = 0.0;
                    }

                    double lum = DirectX.Utils.GetLuminance(entry.Description.Color);
                    Color color = lum < 0.33 ? Colors.White : Colors.Black;

                    canvas.Text.Draw(new Point(intervalPx.Left + TextDrawOffset, Offset + level * RenderParams.BaseHeight), 
                                     entry.Description.Name, 
                                     color,
                                     TextAlignment.Left,
                                     intervalPx.Width - TextDrawOffset);

                    return true;
                });
            });
        }

        public delegate void EventNodeHoverHandler(Rect rect, ThreadRow row, EventNode node);
        public event EventNodeHoverHandler EventNodeHover;

        public delegate void EventNodeSelectedHandler(ThreadRow row, EventFrame frame, EventNode node, ITick tick);
        public event EventNodeSelectedHandler EventNodeSelected;

		public EventFrame FindFrame(ITick tick)
		{
			int index = Data.Utils.BinarySearchExactIndex(EventData.Events, tick.Start);
			if (index >= 0)
			{
				return EventData.Events[index];
			}
			return null;
		}

        public int FindNode(Point point, ThreadScroll scroll, out EventFrame eventFrame, out EventNode eventNode)
        {
            ITick tick = scroll.PixelToTime(point.X);

            int index = Data.Utils.BinarySearchExactIndex(EventData.Events, tick.Start);

            EventFrame resultFrame = null;
            EventNode resultNode = null;
            int resultLevel = -1;

            if (index >= 0)
            {
                EventFrame frame = EventData.Events[index];

                int desiredLevel = (int)(point.Y / RenderParams.BaseHeight);

                frame.CategoriesTree.ForEachChild((node, level) =>
                {
					if (level > desiredLevel || resultFrame != null)
					{
						return false;
					}

                    if (level == desiredLevel)
                    {
                        EventNode evNode = (node as EventNode);
                        if (evNode.Entry.Intersect(tick.Start))
                        {
                            resultFrame = frame;
                            resultNode = evNode;
                            resultLevel = level;
                            return false;
                        }
                    }

                    return true;
                });
            }

            eventFrame = resultFrame;
            eventNode = resultNode;
            return resultLevel;
        }

        public override void OnMouseMove(Point point, ThreadScroll scroll)
        {
            EventNode node = null;
            EventFrame frame = null;
            int level = FindNode(point, scroll, out frame, out node);
            if (level != -1)
            {
                Interval interval = scroll.TimeToPixel(node.Entry);
                Rect rect = new Rect(interval.Left, Offset + level * RenderParams.BaseHeight + RenderParams.BaseMargin, interval.Width, RenderParams.BaseHeight - RenderParams.BaseMargin);
                EventNodeHover(rect, this, node);
            }
            else
            {
                EventNodeHover(new Rect(), this, null);
            }
        }

        public override void OnMouseHover(Point point, ThreadScroll scroll, List<object> dataContext)
        {
            EventNode node = null;
            EventFrame frame = null;
            if (FindNode(point, scroll, out frame, out node) != -1)
            {

                ITick tick = scroll.PixelToTime(point.X);
                int index = Data.Utils.BinarySearchClosestIndex(frame.Synchronization, tick.Start);
                if (index != -1)
                {
					WaitInterval interval = new WaitInterval() { Start = frame.Synchronization[index].Finish, Reason = frame.Synchronization[index].Reason };
					if (index + 1 < frame.Synchronization.Count)
					{
						if (frame.Synchronization[index].Finish < tick.Start && frame.Tick < frame.Synchronization[index + 1].Start)
						{
							interval.Finish = frame.Synchronization[index + 1].Start;
							dataContext.Add(interval);
						}
					}
					else
					{
						if (frame.Synchronization[index].Finish < tick.Start)
						{
							interval.Finish = frame.Finish;
							dataContext.Add(interval);
						}
					}
				}


                dataContext.Add(node);


                // build all intervals inside selected node
                int from = Data.Utils.BinarySearchClosestIndex(frame.Synchronization, node.Entry.Start);
                int to = Data.Utils.BinarySearchClosestIndex(frame.Synchronization, node.Entry.Finish);

				if (from >= 0 && to >= from)
                {
                    IntPair[] waitInfo = new IntPair[(int)SyncReason.SyncReasonCount];

                    for (index = from; index <= to; ++index)
                    {
                        SyncReason reason = frame.Synchronization[index].Reason;
                        int reasonIndex = (int)reason;

                        long idleStart = frame.Synchronization[index].Finish;
                        long idleFinish = (index + 1 < frame.Synchronization.Count) ? frame.Synchronization[index + 1].Start : frame.Finish;

                        if (idleStart > node.Entry.Finish)
                        {
                            continue;
                        }

                        long idleStartClamped = Math.Max(idleStart, node.Entry.Start);
                        long idleFinishClamped = Math.Min(idleFinish, node.Entry.Finish);
                        long durationInTicks = idleFinishClamped - idleStartClamped;
                        waitInfo[reasonIndex].duration += durationInTicks;
                        waitInfo[reasonIndex].count++;
                    }

                    List<NodeWaitInterval> intervals = new List<NodeWaitInterval>();

                    for (int i = 0; i < waitInfo.Length; i++)
                    {
                        if (waitInfo[i].count > 0)
                        {
                            NodeWaitInterval interval = new NodeWaitInterval() { Start = 0, Finish = waitInfo[i].duration, Reason = (SyncReason)i, NodeInterval = node.Entry, Count = waitInfo[i].count };
                            intervals.Add(interval);
                        }
                    }

                    intervals.Sort( (a,b) =>
                    {
                        return Comparer<long>.Default.Compare(b.Finish, a.Finish);
                    });

                    dataContext.AddRange(intervals);
                }

            }
        }

        public override void OnMouseClick(Point point, ThreadScroll scroll)
        {
            EventNode node = null;
            EventFrame frame = null;
            int level = FindNode(point, scroll, out frame, out node);
			if (EventNodeSelected != null)
			{
				ITick tick = scroll.PixelToTime(point.X);
				if (frame == null)
				{
					frame = FindFrame(tick);
				}
				if (frame != null)
				{
					EventNodeSelected(this, frame, node, tick);
				}
			}
        }

        //static Color FilterFrameColor = Colors.LimeGreen;
        //static Color FilterEntryColor = Colors.Tomato;

        static Color FilterFrameColor = Colors.PaleGreen;
        static Color FilterEntryColor = Colors.Salmon;


		static Color GenerateColorFromString(string s)
		{
			int hash = s.GetHashCode();

			byte r = (byte)((hash & 0xFF0000) >> 16);
			byte g = (byte)((hash & 0x00FF00) >> 8);
			byte b = (byte)(hash & 0x0000FF);

			return Color.FromArgb(0xFF, r, g, b);
		}

        public override void ApplyFilter(DirectXCanvas canvas, ThreadScroll scroll, HashSet<EventDescription> descriptions)
        {
            Filter = EventFilter.Create(EventData, descriptions);

            if (Filter != null)
            {
                DynamicMesh builder = canvas.CreateMesh();

                foreach (EventFrame frame in EventData.Events)
                {
                    Interval interval = scroll.TimeToUnit(frame.Header);
                    builder.AddRect(new Rect(interval.Left, 0.0, interval.Width, 1.0), FilterFrameColor);
                }

                foreach (Entry entry in Filter.Entries)
                {
                    Interval interval = scroll.TimeToUnit(entry);
                    builder.AddRect(new Rect(interval.Left, 0.0, interval.Width, 1.0), FilterEntryColor);
                }

                SharpDX.Utilities.Dispose(ref FilterMesh);
                FilterMesh = builder.Freeze(canvas.RenderDevice);
                //if (FilterMesh != null)
                //    FilterMesh.UseAlpha = true;
            }
            else
            {
                SharpDX.Utilities.Dispose(ref FilterMesh);
            }
        }
    }
}