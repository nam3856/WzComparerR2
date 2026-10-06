using System.Reflection;
using Microsoft.Xna.Framework;
using WzComparerR2.MapRender;
using WzComparerR2.MapRender.Patches2;

internal static class MapScrollRegression
{
    public static void Check(FrmMapRender2 preview, MapData map)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var method = typeof(FrmMapRender2).GetMethod("GetMeshBack", flags);
        var batcher = typeof(FrmMapRender2).GetField("batcher", flags).GetValue(preview);
        var release = batcher.GetType().GetMethod("MeshPush");
        var clouds = map.Scene.Back.Slots.Concat(map.Scene.Front.Slots).OfType<BackItem>()
            .Where(back => (back.TileMode & TileMode.ScrollHorizontal) != 0).ToList();
        Smoke.Assert(clouds.Count == 8, "Orbis ship map scrolling fixture changed: " + clouds.Count);

        Vector2 Position(BackItem back, int time)
        {
            int originalTime = back.View.Time;
            try
            {
                back.View.Time = time;
                object mesh = method.Invoke(preview, new object[] { back });
                Smoke.Assert(mesh != null, "Scrolling cloud did not produce a native mesh");
                try { return (Vector2)mesh.GetType().GetProperty("Position").GetValue(mesh); }
                finally { release.Invoke(batcher, new[] { mesh }); }
            }
            finally { back.View.Time = originalTime; }
        }

        foreach (var cloud in clouds)
        {
            int width = cloud.Cx == 0 ? cloud.View.Bounds.Width : cloud.Cx;
            double speed = cloud.Rx * 5d;
            int formerJump = (int)Math.Ceiling(100000d / Math.Abs(speed));
            int tileWrap = (int)Math.Ceiling(width * 1000d / Math.Abs(speed));
            foreach (int time in new[] { 0, formerJump - 1, formerJump + 1, tileWrap - 1, tileWrap + 1, 7200000, int.MaxValue })
            {
                double expected = Math.Floor(cloud.X + speed * time / 1000d % width);
                double actual = Position(cloud, time).X;
                Smoke.Assert(Math.Abs(actual - expected) <= 1, $"Cloud {cloud.BS}/{cloud.No} jumped or overflowed at {time} ms: {actual}, expected {expected}");
            }
            Smoke.Assert(Math.Abs(Position(cloud, formerJump + 1).X - Position(cloud, formerJump - 1).X) <= 2,
                "Cloud still jumps at the former 100 pixel reset");
        }

        // Exercise the new upstream distance and Spine flow semantics through the
        // actual mesh method as well; these must survive the compatibility fix.
        var source = clouds[0];
        var flow = new BackItem { BS = source.BS, No = source.No, X = 12, Y = 34, Alpha = 255,
            Cx = 800, Cy = 600, W = true, Wx = -700, Wy = 400,
            TileMode = TileMode.BothTile, View = new BackItem.ItemView { Animator = source.View.Animator,
                Bounds = source.View.Bounds, FlowX = 7, FlowY = 9 } };
        var position = Position(flow, 4500);
        Smoke.Assert(position.X == 314 && position.Y == 64, "Spine flow speed/distance semantics changed");
        flow.TileMode = TileMode.None;
        position = Position(flow, 4500);
        Smoke.Assert(position.X == 1114 && position.Y == 664, "A nonrepeating flow layer was wrapped");
        Console.WriteLine("MAP_SCROLL_PASS map=200090010 clouds=8 smooth-100px-boundary, tile-wrap, long-uptime, explicit-distance, Spine-flow, nonrepeating-flow");
    }
}
