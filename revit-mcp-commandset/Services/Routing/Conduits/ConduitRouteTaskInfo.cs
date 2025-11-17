// Services/Routing/Conduits/ConduitRouteTask.cs
using System.Collections.Generic;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Services.Routing.Conduits
{
    /// <summary>
    /// 覆寫 Conduit Routing 相關設定（可選）
    /// 之後如果要支援 AI / UI 指定 type / level / size，就用這個區塊。
    /// </summary>
    public class ConduitRouteOverrideOptions
    {

        /// <summary>覆寫用的 ConduitType.Id.IntegerValue（可選）</summary>
        public int? ConduitTypeId { get; set; }

        /// <summary>覆寫用的 Level.Id.IntegerValue（可選）</summary>
        public int? LevelId { get; set; }

        /// <summary>覆寫 conduit 直徑（單位：mm，可選）</summary>
        public double? DiameterMm { get; set; }

        public override string ToString()
        {
            return $"Override(ConduitType={ConduitTypeId}, Level={LevelId}, DiaMm={DiameterMm})";
        }
    }

    /// <summary>
    /// Conduit Routing 任務 DTO。
    /// 目前最重要的是：
    /// - StartElementId
    /// - EndElementId
    /// 其他欄位先預留未來路徑規劃（waypoints / offset / tolerance）。
    /// </summary>
    public class ConduitRouteTaskInfo
    {
        /// <summary>
        /// 起點元素 Id（通常是 CableTray.Id.IntegerValue）。
        /// </summary>
        public int StartElementId { get; set; }

        /// <summary>
        /// 終點元素 Id（panel / box / 既有 conduit…）。
        /// </summary>
        public int EndElementId { get; set; }

        /// <summary>
        /// AI / UI 給的中繼點（世界座標，mm），未來 routing 用。
        /// 目前 Anchor 階段可以先不使用。
        /// </summary>
        public List<JZPoint> Waypoints { get; set; } = new List<JZPoint>();

        /// <summary>
        /// 最短 conduit 段長（mm），用於避免生成太短的段落。
        /// </summary>
        public double MinSegmentLengthMm { get; set; } = 300.0;

        /// <summary>
        /// 從 tray 邊緣推出第一段 conduit 的偏移距離（mm）。
        /// 之後可以用在「不要貼 tray」的設計上。
        /// </summary>
        public double TrayOffsetMm { get; set; } = 50.0;

        /// <summary>
        /// 幾何距離容許值（mm）。
        /// </summary>
        public double ToleranceMm { get; set; } = 10.0;

        /// <summary>
        /// 角度容許值（度數）。
        /// </summary>
        public double ToleranceDeg { get; set; } = 5.0;

        /// <summary>
        /// 覆寫 conduit type / system / level / size 設定（可選）。
        /// </summary>
        public ConduitRouteOverrideOptions Override { get; set; }

        public double ConduitDiameterMm { get; set; } = 53.0;

        public override string ToString()
        {
            return $"ConduitRouteTask(Start={StartElementId}, End={EndElementId}, Waypoints={Waypoints?.Count ?? 0},ConduitDiameterMm={ConduitDiameterMm} ,MinLenMm={MinSegmentLengthMm}, TrayOffsetMm={TrayOffsetMm})";
        }
    }
}
