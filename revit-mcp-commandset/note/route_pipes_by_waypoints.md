
### RevitMCPCommandSet - RoutePipesByWaypoints 指令結構說明
```
RevitMCPCommandSet/
├─ Commands/
│  └─ RoutePipesByWaypoints/
│		├─ RoutePipesByWaypointsCommand.cs          // 入口：接 JSON、RaiseAndWait、回傳結果
│		└─ RoutePipesByWaypointsEventHandler.cs     // 交易域：解析/檢查/建模主流程（起訖解析、逐段建管、放接頭）
│
├─ Models/
│  └─ Common/
│     └─ JZPoint.cs                             // 三維點 DTO，使用原本已經設計好的座標系統
├─ Services/
│  └─ Routing/
│     ├─ RouteTask.cs                          // DTO：AI 傳入的 Task（start/end/waypoints/minSegment 等）
│     ├─ RoutingContextEx.cs                     // 推斷後的上下文（SystemTypeId、PipeTypeId、Level、Diameter、Tolerance）
│     ├─ RoutingServices.cs                    // 規劃核心：分類、推斷、路徑生成、段落建立與收尾對接
│     ├─ SegmentBuilder.cs                     // 建管段邏輯（方向比對、最短段補償、取得遠端 Connector）
│     ├─ FittingPlacer.cs                      // 放置彎頭、Tee/Takeoff 的具體實作
│     ├─ PipeQuery.cs                          // 依拓樸/鄰近找到第一支管、取 PipeType/Level/System
│     └─ ConnectorUtils.cs                     // 族/管 Connector 檢索、最近接點、自由端判定
│
│
├─ Utils/
│  ├─ GeometryUtils.cs                         // 向量/投影/共面與正交判定等
│ 
```

