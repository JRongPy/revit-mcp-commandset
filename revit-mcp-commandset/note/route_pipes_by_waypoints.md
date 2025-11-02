
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

整體計畫說明
1.解析起訖物件
2.型別檢查
3.取得配管資料集

---transaction前---
4.建立 attach 物件
前處理機制將 attach 物件正規化
目標：將各種原始狀況轉換為 Pipe 端點連接的形式，
建構 attach 物件為： Pipe connector 起始 且 con dir != conn.Origin to target point() 

以下幾種狀況
1. Pipe 端點連接
	- con dir == conn.Origin to target point
	- con dir != conn.Origin to target point(目標型態)
2) FamilyInstance
	-	con dir == conn.Origin to target point
	- 	con dir != conn.Origin to target point

3) Pipe 中段連接
	- Takeoff
	- Tee
	- 靠近端點(嘗試退化為端點連接，如果Pipe鄰近的connector 不是free 則放棄連接，只生成管段，並給予提示回饋)

5.組合路徑
6.實際建模