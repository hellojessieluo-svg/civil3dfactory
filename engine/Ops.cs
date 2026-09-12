using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using AcDbPlotType = Autodesk.AutoCAD.DatabaseServices.PlotType;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;
using CivApp = Autodesk.Civil.ApplicationServices.CivilApplication;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;

namespace Civil3DFactory
{
    public class OpDef
    {
        public string Description;
        public string Parameters;
        public bool WritesDrawing;              // true = 会修改 dwg（v1 全为 false）
        public Func<JsonObject, Document, JsonNode> Run;
    }

    /// <summary>
    /// 操作注册表——新增一个操作 = 在这里加一条，之后永久可用（这就是“沉淀”）。
    /// 注意：全程不碰 CivilApplication.ActiveDocument（accoreconsole 里不可用），
    ///       Civil 3D 对象一律从数据库 ModelSpace 遍历取得。
    /// </summary>
    public static partial class Ops
    {
        public static readonly Dictionary<string, OpDef> Registry =
            new Dictionary<string, OpDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["drawing_info"] = new OpDef
            {
                Description = "图纸概况：路线/曲面/图层数量、单位、文件路径",
                Parameters = "(无)",
                WritesDrawing = false,
                Run = DrawingInfo
            },
            ["list_alignments"] = new OpDef
            {
                Description = "列出全部路线：名称、图层、长度、起终点桩号",
                Parameters = "(无)",
                WritesDrawing = false,
                Run = ListAlignments
            },
            ["list_surfaces"] = new OpDef
            {
                Description = "列出全部曲面：名称、类型、图层、高程范围",
                Parameters = "(无)",
                WritesDrawing = false,
                Run = ListSurfaces
            },
            ["export_stations"] = new OpDef
            {
                Description = "按间距导出路线各桩号平面坐标（xlsx/csv）",
                Parameters = "name?(路线名) names?[] all?(bool,默认全部) interval?(默认50) outdir? format?(xlsx|csv|both,默认both)",
                WritesDrawing = false,
                Run = ExportStations
            },
            ["station_elevations"] = new OpDef
            {
                Description = "沿路线按间距采集指定曲面的地面高程（xlsx/csv），曲面外记 null",
                Parameters = "alignment(必需) surface(必需) interval?(默认50) outdir? format?",
                WritesDrawing = false,
                Run = StationElevations
            },
            ["export_surface_grid"] = new OpDef
            {
                Description = "把曲面按网格采样导出 CSV 点云（x,y,z，只读，曲面外的点跳过）。"
                            + "三维可视化/渲染取地形用；走 FindElevationAtXY 逐点查询，样式断链的曲面照样能导"
                            + "（headless 禁止遍历 Vertices/Triangles，会硬崩——见 surface_stats 的注释）",
                Parameters = "surface(必需,曲面名) out(必需,csv绝对路径) minx miny maxx maxy(必需,采样范围) "
                           + "step?(默认10) overwrite?(默认false)",
                WritesDrawing = false,
                Run = ExportSurfaceGrid
            },
            ["create_dwg"] = new OpDef
            {
                Description = "新建一个 DWG 文件并画入图元（圆/直线/圆弧/多段线/文字），不影响当前打开的图纸",
                Parameters = "path(必需,绝对路径) layers?[{name,color}] circles?[{x,y,r,layer}] lines?[{x1,y1,x2,y2,layer}] "
                           + "arcs?[{x,y,r,start_angle,end_angle,layer}(角度制)] polylines?[{points:[[x,y]..],closed,width,layer}] "
                           + "texts?[{x,y,text,height,rotation,layer,style?,width_factor?,color?}] layer?(默认图层,缺省\"0\") overwrite?(默认false)",
                WritesDrawing = true,   // 产生新 dwg 文件（不改动 /i 传入的图纸）
                Run = CreateDwg
            },
            ["list_blocks"] = new OpDef
            {
                Description = "列出图中的块定义（名称、是否带属性、属性标签、已插入次数）",
                Parameters = "include_layout?(默认false,不列 *Model_Space 等系统块)",
                WritesDrawing = false,
                Run = ListBlocks
            },
            ["block_refs"] = new OpDef
            {
                Description = "列出块引用：块名、所在空间、图层、插入点、旋转、比例、包围盒（找图框位置就用它）",
                Parameters = "name?(块名子串筛选，如\"图框\") space?(model|layout|all，默认all) max?(默认200)",
                WritesDrawing = false,
                Run = BlockRefs
            },
            ["dump_block_attributes"] = new OpDef
            {
                Description = "导出块引用的属性到 JSON（图签图框就用它）：逐个块引用报句柄、块名、所在空间和全部「标签→值」",
                Parameters = "name?(块名子串筛选，如\"图框\") space?(model|layout|all，默认all) max?(默认500) "
                           + "with_attributes_only?(默认true，跳过没有属性的块) out?(把结果另写一份 JSON 到该绝对路径)",
                WritesDrawing = false,
                Run = DumpBlockAttributes
            },
            ["set_block_attributes"] = new OpDef
            {
                Description = "按 JSON 反写块属性：逐句柄精确回填，或按块名批量改同一批标签（改图签日期/图号/说明就用它）",
                Parameters = "from?(dump_block_attributes 导出并改过的 JSON 绝对路径) items?[{handle,attributes:{标签:值}}] "
                           + "name?(块名子串，配合 attributes 批量改) attributes?{标签:值} space?(model|layout|all，默认all) "
                           + "width_factors?{标签:因子}(长文字塞窄格：把该标签的属性文字横向压扁，可单独给、"
                           + "不改值也能跑；多行(MText)属性不吃宽度因子，会记进 mtext_skipped 不假装成功) "
                           + "missing_ok?(默认true，图里没有该标签只记录不报错)",
                WritesDrawing = true,
                Run = SetBlockAttributes
            },
            ["model_extents"] = new OpDef
            {
                Description = "模型空间总包围盒 + INSBASE（查\"这张图/这个图例多大、整体插块时基点在哪\"）",
                Parameters = "layer?(图层名子串筛选)",
                WritesDrawing = false,
                Run = ModelExtents
            },
            ["list_marked_regions"] = new OpDef
            {
                Description = "列出模型空间的闭合框候选及其Note/XData/扩展字典/超链接，识别用户标记的成图区域",
                Parameters = "marked_only?(默认true，只返回带附加信息的对象) layer?(图层名子串) max?(默认100)",
                WritesDrawing = false,
                Run = ListMarkedRegions
            },
            ["list_civil_views"] = new OpDef
            {
                Description = "列出模型空间中的横断面图/纵断面图对象及包围盒，用于校核是否落在材料框内",
                Parameters = "type?(section|profile|all，默认all) max?(默认1000)",
                WritesDrawing = false,
                Run = ListCivilViews
            },
            ["modify_dwg"] = new OpDef
            {
                Description = "改动既有图纸（画图元/插块）。**默认副本预演**，不碰原图；apply:true 才写回原图并自动备份",
                Parameters = "dwg(必需,目标图纸) apply?(默认false=预演到副本) backup?(默认true,apply时先备份) "
                           + "space?(默认Model,可给布局名；对图元与填充生效，块另有逐项 space) "
                           + "blocks?[{name,from_dwg?,source_block?,x,y,scale?,rotation?,layer?,space?(布局名),attributes?{标签:值}}] "
                           + "circles?/lines?/arcs?/polylines?/texts?/layers?(同 create_dwg；线/文字均可给 color=ACI 色号，文字另可给 style/width_factor) "
                           + "hatches?[{points:[[x,y],...],pattern?(默认SOLID),scale?,angle?,layer?,color?}] "
                           + "xrefs?[{path(必需,绝对路径),name?,x?,y?,layer?,overlay?}](挂外参+插引用进模型；"
                           + "同名已在就跳过可重跑；相对路径外参会被打印器搬家搬丢，一律绝对路径) "
                           + "out?(预演文件路径,缺省自动命名)",
                WritesDrawing = true,
                Run = ModifyDwg
            },
            ["plot_pdf"] = new OpDef
            {
                Description = "把图纸打印成 PDF（DWG To PDF.pc3）。默认打印当前图纸模型空间范围，也可指定窗口或外部 dwg",
                Parameters = "out(必需,pdf路径) dwg?(打印外部文件,缺省用当前图纸) layout?(默认Model) "
                           + "window?{minx,miny,maxx,maxy}(模型空间=世界坐标自动换DCS；纸空间布局=布局图纸坐标mm，"
                           + "2026-08-28 起支持——从横排多图框合并布局逐框抠单页 A3 用它) "
                           + "paper?(默认A3) ctb?(默认monochrome.ctb,传\"\"则彩色) "
                           + "lineweights?(默认true) overwrite?(默认false) "
                           + "fit?(默认false；只对纸空间布局有效：按布局图形范围缩放到整纸打一页，"
                           + "用来把横排多图框的总览布局一页出全。默认 false 仍是 Layout 类型 1:1，"
                           + "纸有多大只截多大)",
                WritesDrawing = false,  // 只产出 PDF，不改图纸
                Run = PlotPdf
            },
            ["normalize_textstyles"] = new OpDef
            {
                Description = "宿主外部节点：把 -黑体/txt1 两个文字样式归化到标准字体（打印前强制节点），另存副本不改原图",
                Parameters = "out(必需,另存路径) overwrite? visible? timeout_sec?；实际执行入口 nodes/normalize_textstyles/run.ps1",
                WritesDrawing = false,
                Run = RunNodeNormalizeTextStyles
            },
            ["plot_attribute_titleblocks"] = new OpDef
            {
                Description = "宿主外部节点：用锁定版本的 ACC 专用 EXE 批量打印模型空间和布局空间属性图框",
                Parameters = "output_directory(必需)；实际执行入口 nodes/plot_attribute_titleblocks/run.ps1",
                WritesDrawing = false,
                Run = RunNodePlotAttributeTitleblocks
            },
            // ── 从一条直线到工程量的整条链（实现见 Ops.Chain.cs）────────────────
            // 全部只改内存中的图纸，最后必须显式 save_dwg 才落盘。
            ["check_sac_paths"] = new OpDef
            {
                Description = "流水线前置节点：检查两个固定 PKT；失联时自动原位替换 SAC 子装配并复检",
                Parameters = "assembly(必需) paths(必需且只能为左右两个绝对 PKT 路径)",
                WritesDrawing = true,
                Run = RunNodeCheckSacPaths
            },
            ["create_surface_grid"] = new OpDef
            {
                Description = "造一块网格原地形曲面（做链路自测用；真实项目用已有地形曲面即可）",
                Parameters = "name(必需) minx miny maxx maxy(必需) step?(默认20) elev?(默认0) slope_x? slope_y?(每米高差) style?",
                WritesDrawing = true,
                Run = CreateSurfaceGrid
            },
            ["create_alignment"] = new OpDef
            {
                Description = "画一条线并把它定义为路线（也可把图中已有的线转成路线）",
                Parameters = "name(必需) points?[[x,y],...](现画一条) handle?(图中已有的线) style? label_set? site? layer? "
                           + "erase_source?(默认true) add_curves?(默认false)",
                WritesDrawing = true,
                Run = RunNodeCreateAlignment
            },
            ["alignment_to_polyline"] = new OpDef
            {
                Description = "把路线的平面几何转成普通多段线（直线/圆弧精确，缓和曲线按步长采样）——出纯 CAD 图用",
                Parameters = "alignment(路线名) 或 handle(路线句柄) 二选一 layer?(默认\"5-中心线\") color_index?(默认3绿) "
                           + "color_bylayer?(默认false) linetype?(默认CENTER2) linetype_file?(默认acadiso.lin) "
                           + "linetype_scale?(默认5) elevation?(默认0) spiral_step?(默认5) erase_source?(默认false)",
                WritesDrawing = true,
                Run = RunNodeAlignmentToPolyline
            },
            ["export_alignments_to_dwg"] = new OpDef
            {
                Description = "把路线抽成纯多段线写进一张全新空白 DWG（无任何 Civil 对象）；偏移路线按 ParentAlignmentId 自动归到父通道并分左右",
                Parameters = "out(必需,绝对路径) overwrite?(默认false) alignments?([]只导这几条) centers_only?(默认false,只导中心线) "
                           + "center_layer?(默认\"CL-{channel}\") edge_layer?(默认\"EDGE-{channel}-{side}\") "
                           + "center_color?(默认3) edge_color?(默认4) center_linetype?(默认CENTER2) edge_linetype?(默认Continuous) "
                           + "linetype_scale?(默认5) spiral_step?(默认5)",
                WritesDrawing = true,
                Run = RunNodeExportAlignmentsToDwg
            },
            ["export_corridor_feature_lines"] = new OpDef
            {
                Description = "把走廊要素线（按点码）抽成三维多段线写进一张全新空白 DWG（无 Civil 对象），逐点保留高程；点码沿用 PKT 码表（daylight/toe/mp/controlpoint…±left/right）",
                Parameters = "out(必需,绝对路径) overwrite?(默认false) corridors?([]只导这几个走廊) codes?([]只导这些点码,默认全部) "
                           + "layer?(默认\"FL-{corridor}-{code}\",占位符 {corridor}/{baseline}/{code}) color?(默认2) min_points?(默认2)",
                WritesDrawing = true,
                Run = RunNodeExportCorridorFeatureLines
            },
            ["dump_corridor_params"] = new OpDef
            {
                Description = "摊平走廊模型全部设计参数：走廊→基线→区域→装配→子装配→每个参数的显示名和当前值（只读）",
                Parameters = "corridors?(默认true) assemblies?(默认true) assembly?(只导某一个装配)",
                WritesDrawing = false,
                Run = RunNodeDumpCorridorParams
            },
            ["list_polylines"] = new OpDef
            {
                Description = "按图层列出模型空间多段线：句柄、长度、顶点数、圆弧段数、起终点（给 replace_alignment_geometry 找源线用）",
                Parameters = "layer?(精确匹配,缺省全部) min_length?(默认0) max?(默认200)",
                WritesDrawing = false,
                Run = RunNodeListPolylines
            },
            ["sample_polyline_elevations"] = new OpDef
            {
                Description = "按多段线取曲面高程（只读）：图层或句柄白名单上每条多段线逐顶点采指定曲面，"
                            + "报最低/最高/平均/中位高程与落在曲面外的点数；**开口线照收**，"
                            + "这是它跟 survey_dredge_regions（只认闭合边界、只报聚合值）的分工。"
                            + "拿到一圈设计边线先看脚下原地形有多高时用它；出量走 calculate_surface_volume",
                Parameters = "surface(必需,曲面名) layer?(与 handles 至少给一个) handles?([]句柄白名单) "
                           + "names?({句柄:名称},给了就把编号带进结果) step?(默认0=只取顶点,>0 在顶点之间等距加密) "
                           + "interior_step?(默认0;>0 时另在闭合线**面内**打网格采样,出 z_in_*——岛屿/地块的「现状高程」取这个,不是边线一圈) "
                           + "include_open?(默认true,开口线也算) min_length?(默认0) points?(默认false,结果里带逐点[x,y,z]) "
                           + "export_excel?(默认false,出汇总+逐点两张表) outdir? excel_format?(xlsx|csv|both,默认xlsx)",
                WritesDrawing = false,
                Run = RunNodeSamplePolylineElevations
            },
            ["replace_alignment_geometry"] = new OpDef
            {
                Description = "原位重写路线几何：清空实体集并按多段线逐段重建（全 Fixed 实体）。ObjectId 不变，偏移路线/纵断面/采样线等挂接保留",
                Parameters = "alignment(路线名) 或 alignment_handle 二选一 polyline(必需,多段线句柄) erase_polyline?(默认false)",
                WritesDrawing = true,
                Run = RunNodeReplaceAlignmentGeometry
            },
            ["import_design_lines"] = new OpDef
            {
                Description = "S01：把设计线 DWG 搬进当前图，中心线-{通道} 转成路线，边界-{通道} 留作宽度目标源（身份靠图层名）",
                Parameters = "dwg(必需,设计线图纸绝对路径) center_prefix?(默认\"中心线-\") boundary_prefix?(默认\"边界-\") "
                           + "style? label_set? alignment_layer? add_curves?(默认false) replace_existing?(默认true) "
                           + "min_boundary_area?(默认100,小于此面积的闭合线当碎片丢)",
                WritesDrawing = true,
                Run = RunNodeImportDesignLines
            },
            ["create_corridor_regions"] = new OpDef
            {
                Description = "S04：建多区域走廊，每段用各自装配（既有 create_corridor 只能单区域）；桩号按路线实际范围裁剪",
                Parameters = "alignment(必需) surface(必需,原地形) regions(必需,[{assembly,start,end,name?}]) "
                           + "name?(默认\"{路线}_走廊\") baseline?(默认\"{路线}_基线\")",
                WritesDrawing = true,
                Run = RunNodeCreateCorridorRegions
            },
            ["create_design_profiles"] = new OpDef
            {
                Description = "S03：建地面线+设计线；端部地面高于底高程时按 1:10 放坡接地形（坡长由高差算），否则平坡",
                Parameters = "surface(必需,原地形曲面名) design_elev?(默认3.0) end_slope?(默认10,即1:10) "
                           + "ramps?({通道:[\"start\",\"end\"]},逐端指定放坡,缺省全平坡) "
                           + "ramp_tolerance?(默认0.05,高差小于它不放坡) channels?([]只做这几条) "
                           + "ground_name?(默认\"{channel}-地面\") design_name?(默认\"{channel}-设计\") "
                           + "ground_style? design_style? label_set? replace_existing?(默认true)",
                WritesDrawing = true,
                Run = RunNodeCreateDesignProfiles
            },
            ["offsets_from_boundary"] = new OpDef
            {
                Description = "S02：闭合边界劈成左右偏移路线 {通道}_左/{通道}_右（走廊宽度目标），按桩号分桶取左右极值并抽稀",
                Parameters = "boundary_prefix?(默认\"边界-\") interval?(默认25,采样与抽稀步长) channel?(只做某一条) "
                           + "style? label_set? erase_boundary?(默认false) replace_existing?(默认true) min_boundary_area?(默认100)",
                WritesDrawing = true,
                Run = RunNodeOffsetsFromBoundary
            },
            ["measure_channel_width"] = new OpDef
            {
                Description = "量每条通道沿程实际疏浚宽度：边界顶点投影到中心线取(桩号,偏距)分桶统计左右半宽（通道是变宽的，单个平均上口宽表达不了）",
                Parameters = "interval?(默认50) min_area?(默认100,小于此面积的边界当碎片丢) channel?(只量某一条)",
                WritesDrawing = false,
                Run = RunNodeMeasureChannelWidth
            },
            ["export_design_lines"] = new OpDef
            {
                Description = "导出建模输入线到全新空白 DWG：中心线/边线/道路边界（闭合）三类，图层即身份；边线归属与左右按 StationOffset 实测",
                Parameters = "out(必需,绝对路径) overwrite?(默认false) boundaries?(默认true,是否导走廊曲面外边界) "
                           + "center_layer?(默认\"CL-{channel}\") edge_layer?(默认\"EDGE-{channel}-{side}\") "
                           + "boundary_layer?(默认\"BOUNDARY-{channel}\") center_color?(3) edge_color?(4) boundary_color?(2) "
                           + "center_linetype?(CENTER2) edge_linetype?(Continuous) boundary_linetype?(Continuous) "
                           + "linetype_scale?(默认5) spiral_step?(默认5) probe_points?(默认20) offset_tolerance?(默认200)",
                WritesDrawing = true,
                Run = RunNodeExportDesignLines
            },
            ["offset_alignment"] = new OpDef
            {
                Description = "按距离生成左右偏移路线（名字带 _左/_右 前缀，走廊那步靠它找目标）",
                Parameters = "alignment(必需) distance?(默认15) offsets?[数=整条; 对象{distance,start_station,end_station,name?}=分段偏移] style?",
                WritesDrawing = true,
                Run = RunNodeOffsetAlignment
            },
            ["create_connected_alignment"] = new OpDef
            {
                Description = "两条路线间按半径建连接路线（交叉口转角，动态跟随父路线；配分段偏移用）",
                Parameters = "name(必需) in_alignment/in_station(必需) out_alignment/out_station(必需) radius?(默认20) "
                           + "greater_than_180?(默认false) offset_in?/offset_out?(默认0) style? label_set?",
                WritesDrawing = true,
                Run = RunNodeCreateConnectedAlignment
            },
            ["create_fillet_alignment"] = new OpDef
            {
                Description = "约束正确的转角路线：固定线+自由圆弧(相切,半径参数)+固定线（改半径自动保切）",
                Parameters = "name(必需) line1:[[x,y],[x,y]](必需) line2:同(必需) radius?(默认20) greater_than_180?(默认false) style? label_set?",
                WritesDrawing = true,
                Run = RunNodeCreateFilletAlignment
            },
            ["trim_offsets_to_corners"] = new OpDef
            {
                Description = "台田角收口：偏移段区间端原位缩回连接路线弧切点外（不删不重建，角存活）",
                Parameters = "corners:[{corner,in_alignment,out_alignment},...](必需) margin?(默认0.05)",
                WritesDrawing = true,
                Run = RunNodeTrimOffsetsToCorners
            },
            ["rebuild_fillet_network"] = new OpDef
            {
                Description = "转角网络重铸（点对点契约）：按配对表逐角解析重解并原位重建（缺则新建），"
                            + "写 C3DF_FILLET XData 认亲（WaterBox 联动用），父段区间端精确收到腿外端——段尾点==角首点，"
                            + "报账带每个连接点实测缝宽",
                Parameters = "corners:[{corner,a,b,radius?},...](必需) radius?(默认20) leg?(默认0.1) erase?([]先清这些废件)",
                WritesDrawing = true,
                Run = RunNodeRebuildFilletNetwork
            },
            ["assign_layers"] = new OpDef
            {
                Description = "批量归层：按分组把路线（按名）/实体（按句柄）挪到指定图层，图层不存在就建（带色号）；只动 Layer 不碰几何",
                Parameters = "groups:[{layer(必需) color?(ACI,默认7) alignments?([]路线名) handles?([]句柄)},...](必需)",
                WritesDrawing = true,
                Run = RunNodeAssignLayers
            },
            ["list_site_parcels"] = new OpDef
            {
                Description = "宗地清点（只读）：逐站点报宗地数+逐宗名称/面积（台田成环对账用）。路线挪站点托管API没有，人走 Prospector 多选右键移动到站点",
                Parameters = "site?(只看这个站点,缺省全部)",
                WritesDrawing = false,
                Run = RunNodeListSiteParcels
            },
            ["draw_polylines"] = new OpDef
            {
                Description = "往宿主图批量画多段线（带 bulge 弧段）：台田边界等派生物落图；clear_layers 先清旧线保证重跑幂等",
                Parameters = "polylines:[{vertices:[[x,y,bulge?],...](必需) layer? color? closed?(默认true) name?},...](必需) "
                           + "layer?(默认\"0\") color?(默认7) clear_layers?([]先清这些层上的多段线)",
                WritesDrawing = true,
                Run = RunNodeDrawPolylines
            },
            ["draw_table"] = new OpDef
            {
                Description = "自画数据表（纯CAD线+文字）：rows 二维数组直接画成表格，模型空间或指定布局都行；工程量表上图用，"
                            + "替代 OLE（OLE 无头贴不进、改不了、accore 打不出）。实体带 XData(C3DF_TBL:name) 认亲，同名重跑先清旧表",
                Parameters = "rows(必需,[[单元格,...],...]) x,y(必需,表左上角;布局=图纸mm) space?(默认Model,或布局名) "
                           + "width?(总宽,给了按比例缩放各列) col_widths?([]逐列宽) row_height?(默认5) text_height?(默认2.5) "
                           + "header_rows?(默认1) header_row_height?(默认=row_height) layer?(默认C3DF-TABLE) color?(默认7) "
                           + "text_style?(缺省自动挑 -黑体) name?(默认\"表\",清旧表的认亲标签) clear?(默认true) "
                           + "mask?(默认false,表底垫 Wipeout 白底,遮住视口里的模型内容) lineweight?(图层线宽mm,默认0.25,0=不设)；"
                           + "新实体一律 draworder 置顶（布局里视口常被置前，不置顶会被视口内容压住）",
                WritesDrawing = true,
                Run = RunNodeDrawTable
            },
            ["set_offset_width"] = new OpDef
            {
                Description = "改通道半宽（无头版）：主线全部偏移子线 NominalOffset=±width；报账带改后回读值防快照属性安静失败",
                Parameters = "alignment(必需,主线名) width?(默认15)",
                WritesDrawing = true,
                Run = RunNodeSetOffsetWidth
            },
            ["list_offset_widths"] = new OpDef
            {
                Description = "偏移宽度清单（只读）：每条偏移路线的父线/NominalOffset/区间数（成田逐通道宽度对账用）",
                Parameters = "无",
                WritesDrawing = false,
                Run = RunNodeListOffsetWidths
            },
            ["erase_alignments"] = new OpDef
            {
                Description = "批量删路线：按名或按句柄（句柄免疫名字编码问题）；找不到的只报不炸",
                Parameters = "names?([]路线名) handles?([]句柄) 至少给一个",
                WritesDrawing = true,
                Run = RunNodeEraseAlignments
            },
            ["create_profiles"] = new OpDef
            {
                Description = "生成地面线（从曲面采）+ 设计线（平坡）",
                Parameters = "alignment(必需) surface(必需) design_elev?(默认0) ground_style? design_style? label_set?",
                WritesDrawing = true,
                Run = RunNodeCreateProfiles
            },
            ["create_corridor"] = new OpDef
            {
                Description = "建走廊并设目标（曲面槽→原地形，偏移槽→左右偏移路线）",
                Parameters = "alignment(必需) assembly(必需,用 civil_env 查名) surface(必需,原地形) name? baseline? region?",
                WritesDrawing = true,
                Run = RunNodeCreateCorridor
            },
            ["create_corridor_surface"] = new OpDef
            {
                Description = "在走廊上建道路曲面（默认取带 Top 的链接代码，取不到则全取）+ 外边界",
                Parameters = "alignment(必需) corridor? name? link_codes?[] boundary?(默认true)",
                WritesDrawing = true,
                Run = RunNodeCreateCorridorSurface
            },
            ["create_sample_lines"] = new OpDef
            {
                Description = "建采样线组（建前清空本路线所有旧组），并把原地形/走廊/道路曲面设为已采样；支持等间距或显式端点两种模式",
                Parameters = "alignment(必需) surface(必需,原地形) interval?(默认50) swath?(默认50) style? corridor? road_surface? "
                           + "lines?([{name?,points:[[x,y],...]}],给了就按显式端点建线,忽略interval/swath)",
                WritesDrawing = true,
                Run = RunNodeCreateSampleLines
            },
            ["import_surface"] = new OpDef
            {
                Description = "从外部 DWG 把指定名称的 TIN 曲面 WblockClone 进当前图纸（已存在同名则跳过）",
                Parameters = "dwg(必需,来源图纸绝对路径) name(必需,曲面名)",
                WritesDrawing = true,
                Run = RunNodeImportSurface
            },
            ["create_dike_sample_lines"] = new OpDef
            {
                Description = "堤埝批处理：中线图层逐条转路线（起点按沿线K桩号文字定向），按图上已画断面线建采样线组并采指定原地形；原中线保留",
                Parameters = "centerline_layers(必需,[]中线图层名) surface(必需,原地形曲面名) number_layer?(默认6堤埝编号) "
                           + "station_layer?(默认0) section_layers?([]，默认[2横断面线]) number_max_dist?(150) station_max_dist?(60) "
                           + "section_margin?(120) fallback_swath?(50,无相交断面线时按桩号文字兜底的单侧宽) "
                           + "rebuild_only?(默认false;true=路线已存在只重建采样线组,走廊建成后用) corridor_suffix?(_走廊) road_surface_suffix?(_道路曲面)",
                WritesDrawing = true,
                Run = RunNodeCreateDikeSampleLines
            },
            ["extract_measured_sections"] = new OpDef
            {
                Description = "测量断面标定与地面线提取（只读）：一张图平铺多个断面的测量图，按图层约定"
                            + "（地面线 dmx*、刻度文字 zdmt*、图名 1-图名*）逐断面用刻度文字最小二乘标定图纸→工程坐标，"
                            + "报每断面 PASS/FAIL、残差、桩号、测点数、堤顶高程。拆堤链路第一步，参数怎么给靠它的输出推",
                Parameters = "ground_layer?(默认dmx*,支持*通配) column_layer?(默认zdmt*) title_layer?(默认1-图名*) "
                           + "title_regex?(图名解析式,默认按「测线名-K公里+米断面」,须含 ln/km/m 三个命名组) "
                           + "offset_tolerance?(0.06) elev_tolerance?(0.02) table_depth?(80,向下找刻度文字的深度,图纸单位) "
                           + "pair_tol_x?(3,刻度文字与顶点X配对容差) line_filter?(正则,只处理匹配的测线/图名) "
                           + "include_ground_points?(默认false,true=输出每断面全部(偏距,高程))",
                WritesDrawing = false,
                Run = RunNodeExtractMeasuredSections
            },
            ["generate_demolition_design_lines"] = new OpDef
            {
                Description = "拆堤断面设计线：逐断面画清表线（地面高于阈值的段下移清表厚度）、开挖线"
                            + "（清表后表面高于底高程的段，限宽 ±half_width，越界端按 1:m 放坡至接地）、"
                            + "ANSI31/ANSI37 填充与引出线标注。搬自拆堤插件 V1 的 C3DF-GenDesignLine；"
                            + "画完可人工改边界，方量由 compute_embankment_demolition 按图上实际线重算",
                Parameters = "ground_layer? column_layer? title_layer? title_regex? offset_tolerance? elev_tolerance? "
                           + "table_depth? pair_tol_x? line_filter?(同 extract_measured_sections) "
                           + "strip_threshold_elev?(6.5,清表阈值高程;设到堤顶以上即不清表) strip_thickness?(0.3,清表厚度) "
                           + "bottom_elev?(5.0,设计拆堤底高程) bottom_elev_by_section?({图名或测线名:高程},逐断面覆盖) "
                           + "slope_ratio_m?(3.0,放坡1:m的m) excavation_half_width?(8.0,距中心线开挖半宽,仅放坡侧生效) "
                           + "strip_line_layer?(C3DF-STRIP-LINE) excavation_line_layer?(C3DF-CUT-LINE) hatch_layer?(C3DF-HATCH) "
                           + "annotation_layer?(C3DF-LABEL) centerline_layer?(C3DF-CL) hatch_scale?(15) text_height?(2.5,图纸单位) "
                           + "text_style?(标注文字样式,缺省用图纸当前样式;默认样式配 txt.shx 时中文会成 ????,那就指一个中文样式或先跑 normalize_textstyles) "
                           + "draw_hatch?(true) draw_labels?(true) clear_existing?(true,重跑前清掉这5个图层上的线/填充/文字)",
                WritesDrawing = true,
                Run = RunNodeGenerateDemolitionDesignLines
            },
            ["compute_embankment_demolition"] = new OpDef
            {
                Description = "拆堤工程量（平均断面法）：读图上实际的清表线/开挖线（允许人工修过），"
                            + "逐断面量清表面积与底土开挖面积并标在断面上方，同测线相邻桩号按平均断面法出方量表（xlsx/csv）。"
                            + "搬自拆堤插件 V1 的 C3DF-CalcVolume；须在设计线画完（并修完）之后跑",
                Parameters = "ground_layer? column_layer? title_layer? title_regex? offset_tolerance? elev_tolerance? "
                           + "table_depth? pair_tol_x? line_filter?(同 extract_measured_sections) "
                           + "strip_line_layer?(C3DF-STRIP-LINE) excavation_line_layer?(C3DF-CUT-LINE) own_margin?(30,设计线归属断面的包围盒外扩,图纸单位) "
                           + "annotate_sections?(true,在断面上方写面积) annotation_layer?(C3DF-QTY-LABEL) text_height?(2.5) "
                           + "text_style?(同 generate_demolition_design_lines) "
                           + "clear_existing?(true,只清 annotation_layer) export_excel?(true) outdir? excel_out_path? excel_format?(xlsx|csv|both)",
                WritesDrawing = true,
                Run = RunNodeComputeEmbankmentDemolition
            },
            ["compute_quantities"] = new OpDef
            {
                Description = "按工程量准则算材质列表，得到逐桩号挖填方",
                Parameters = "alignment(必需) surface(必需,原地形) criteria(必需,准则名) road_surface_slot?(默认\"道路曲面\") road_surface? corridor?",
                WritesDrawing = true,
                Run = RunNodeComputeQuantities
            },
            ["create_profile_view"] = new OpDef
            {
                Description = "在模型空间出纵断面图（地面线+设计线都会画进去）",
                Parameters = "alignment(必需) x? y?(缺省摆在路线起点下方200m) style? band_set? name?",
                WritesDrawing = true,
                Run = RunNodeCreateProfileView
            },
            ["create_section_views"] = new OpDef
            {
                Description = "在模型空间出横断面图：按采样线逐张出图 + 网格摆放 + 体积表格",
                Parameters = "alignment(必需) group?(采样线组名,缺省找<路线>_采样线组,找不到且只有一个组则用它) "
                           + "style? code_set? section_style?(地面线断面样式,如 @C3DF-GroundLine) elev_min? elev_max?(min>=max则自动) "
                           + "offset_left?(50) offset_right?(50) x? y? rows?(2) cols?(2) col_spacing?(130) row_spacing?(45) group_spacing?(0) "
                           + "volume_table?(默认true) corridor?",
                WritesDrawing = true,
                Run = RunNodeCreateSectionViews
            },
            ["dump_label_styles"] = new OpDef
            {
                Description = "只读诊断：反射摊平标签样式（LabelStyle）的文本组件内容与偏移。"
                            + "标签在图上显示的固定文字藏在样式的文本组件里，DXFOUT 落不到明文（Civil 对象走 proxy），"
                            + "只能靠 API 读；要调标签位置先用它认准是哪个样式、哪个组件在出这行字",
                Parameters = "contains?(只报摊平结果里含这个串的样式，如 疏浚控制线；不给则全报) "
                           + "depth?(摊平深度,默认4) max_styles?(最多扫多少个样式,默认400)。"
                           + "每个组件报 anchor_component/anchor_location（锚在哪个组件的哪个点）＋attachment；"
                           + "样式级报 dragged_state（拖曳状态页：DisplayType/TextHeight/LeaderType…）与 leader",
                WritesDrawing = false,
                Run = RunNodeDumpLabelStyles
            },
            ["list_styles"] = new OpDef
            {
                Description = "只读侦察：列出图里各类 Civil 样式集合的名字（纵断面视图样式/带状图集/标签集/断面图样式…）。"
                            + "换样式或跨图搬样式前，先用它对照两张图有没有同名样式",
                Parameters = "contains?(只报集合名含该串的，如 Profile / Band / LabelSet)",
                WritesDrawing = false,
                Run = RunNodeListStyles
            },
            ["dedupe_entities"] = new OpDef
            {
                Description = "模型空间同位置同内容实体去重（MTEXT/TEXT/LINE/PLINE/块参照/SOLID），"
                            + "可选按内容匹配平移 MTEXT。项目B初设横断面 655 个引擎画两遍的重合标签在导出件上就靠它清",
                Parameters = "layer?(限定图层) tol?(位置量化精度米,默认0.001) "
                           + "moves?(数组：{contains, dx?, dy?}，去重后把内容含 contains 的文字平移)",
                WritesDrawing = true,
                Run = RunNodeDedupeEntities
            },
            ["set_label_style"] = new OpDef
            {
                Description = "改标签样式组件：可见性/文本偏移/文本内容/线角度长度。样式在 LabelStyles 根和 "
                            + "CodeSetStyles 支里找（与 dump_label_styles 同源）。项目B初设横断面 655 个双份标签"
                            + "（code set 分支+marker 分支各画一遍）就靠它关掉一支",
                Parameters = "style(标签样式名,必填) components(数组,必填)：每项 {name(组件名,*=全部), "
                           + "visible?, x_offset?, y_offset?, contents?, angle_deg?, length?, height?, "
                           + "attachment?(TopCenter/MiddleCenter/BottomCenter…), anchor_component?(组件名或<Feature>), "
                           + "anchor_location?(TopCenter/BottomCenter…)} "
                           + "dragged_state?({属性名:值}，键名照 dump_label_styles 报的 dragged_state，如 DisplayType/TextHeight)",
                WritesDrawing = true,
                Run = RunNodeSetLabelStyle
            },
            ["add_note_label"] = new OpDef
            {
                Description = "批量放通用标签（General Note Label）：按点位逐点放指定样式的标签。坐标标注自动化走这里——"
                            + "样式里写 <[Northing]>/<[Easting]> 字段，落点即取值、拖动随动，不用手抄坐标。"
                            + "点可带 dx/dy 直接拖成拖曳态（看拖曳排版或批量引出）",
                Parameters = "style(通用标签样式名,必填,大小写敏感) points(必填,[{x,y,dx?,dy?}] dx/dy=拖曳偏移,图形单位) layer?(放到哪层,没有就建)",
                WritesDrawing = true,
                Run = RunNodeAddNoteLabel
            },
            ["dump_material_styles"] = new OpDef
            {
                Description = "只读诊断：摊平「材质列表→材质→样式」与 MaterialSection 实体的样式指向（反射），查材质填充用错样式",
                Parameters = "sample?(MaterialSection 抽样条数,默认5)",
                WritesDrawing = false,
                Run = RunNodeDumpMaterialStyles
            },
            ["restyle_section_views"] = new OpDef
            {
                Description = "给已存在的横断面图就地换样式/定高程，不删不重建（create_section_views 覆盖重建"
                            + "要先删带体积表的旧视图，之后 save_dwg 必报 eWasOpenForWrite；本节点绕开删除路径，"
                            + "且完全不碰采样线、材质列表、体积表和工程量）",
                Parameters = "alignment?(缺省全部路线) style?(断面图样式) section_style?(地面线断面样式) "
                           + "material_style?(材质断面样式,缺省跟 section_style) "
                           + "material_shape_style?(材质填充样式=ShapeStyles,如 @C3DF-CutFill；挂在材质列表 QTOMaterial.ShapeStyleId 上) "
                           + "corridor_surface_style?(道路曲面断面样式，指个不打印样式即隐藏道路曲面线) "
                           + "elev_min? elev_max?(min<max 才生效) elev_auto?(显式回自动高程)；至少给一样。"
                           + "material_style 先查 SectionStyles 再查 ShapeStyles（@C3DF-CutFill 在后者）；不给就不碰，"
                           + "别拿地面线样式垫底冲掉用户手设的值。走廊本体断面的代码集样式不碰",
                WritesDrawing = true,
                Run = RunNodeRestyleSectionViews
            },
            ["restyle_profile_view_bands"] = new OpDef
            {
                Description = "给已存在的纵断面图换标注栏（Band）样式：按「旧样式名→新样式名」映射就地替换，"
                            + "不删不重建、不碰标注栏数据源和视图样式（create_profile_view 只能建图时套整套 band set，"
                            + "对已出好的视图无能为力；GUI 里只能一张张开 Profile View Properties → Bands 改）",
                Parameters = "bottom?([]底部标注栏样式名,按从上到下的位置一一对应,null/空串=该位置不动) top?([]同理) 至少给一个；"
                           + "views?([]视图名,缺省全部纵断面图) dry_run?(默认false,true 只报告不落盘)。"
                           + "样式名要在图里 BandStyles 下精确存在，找不到直接报错不兜底；"
                           + "band 条数与数组长度不等的视图整张跳过（views_mismatched），绝不半写。"
                           + "**只能按位置换**：BandStyleId 在 Civil 2025 API 里只写不可读，读不出旧样式名，"
                           + "做不了「认名换名」——先 dry_run 看 per_view.fingerprint 各视图是否逐位置同构再落盘",
                WritesDrawing = true,
                Run = RunNodeRestyleProfileViewBands
            },
            ["sync_corridor_range"] = new OpDef
            {
                Description = "走廊区间对齐路线起终点（换中线后配套）：逐基线首区间起点/末区间终点拉到路线两端，中间区间收进范围，然后 Rebuild",
                Parameters = "corridor(必需,走廊名)",
                WritesDrawing = true,
                Run = RunNodeSyncCorridorRange
            },
            ["refresh_sample_lines"] = new OpDef
            {
                Description = "采样线组不动、组内线沿新几何重生（换中线后配套；create_sample_lines 是删组重建，本节点保组保采样源）",
                Parameters = "alignment(必需) interval?(默认按现有线数反推) swath?(默认按现有线长反推)",
                WritesDrawing = true,
                Run = RunNodeRefreshSampleLines
            },
            ["restore_sample_line_labels"] = new OpDef
            {
                Description = "把被删光的采样线标签（桩号名字）重建回来：一个采样线组建一个 SampleLineLabelGroup。"
                            + "⚠ SampleLine 上没有 LabelStyleId，标签不是挂在线上而是挂在组上的独立实体，"
                            + "「给每条线赋标签样式」这条路不通；导出前 ERASE 采样线族会连标签一起清光，就用本节点补回",
                Parameters = "style?(采样线标签样式名,默认 @C3DF-AlignmentStation,大小写敏感) alignment?(只修这条路线的组,缺省全图) "
                           + "skip_existing?(默认true,已有标签组的跳过) dry_run?(默认false,只盘点不建)。"
                           + "验收内建：标签组数=采样线组数 且 SubEntityCount 合计=采样线总数，不到数抛异常不提交",
                WritesDrawing = true,
                Run = RunNodeRestoreSampleLineLabels
            },
            ["arrange_section_sheets"] = new OpDef
            {
                Description = "横断面图排版真源：把断面图集中摆到全部路线右侧。layout=rows(默认)一条路线一行、行首写路线名；"
                            + "layout=sheets 图框排成网格。改 Location 不重建，样式标注全保留。"
                            + "批量出图走「create_section_views 生成 → 本节点统一排版」；create_section_views 自带的 x/y/spacing 只用于单路线自测",
                Parameters = "layout?(rows|sheets,默认rows) scale?(默认200,出图比例) paper?(默认A3) paper_w? paper_h?(mm) "
                           + "rows?(1) cols?(1,一个图框内的断面网格;A3@1:200只放得下1张) "
                           + "row_pitch?/col_pitch?(模型单位,相邻断面中心距;0=按图框均分。断面比格子矮时均分会留白,这是收紧行距的旋钮) "
                           + "sheets_per_row?(10,仅sheets版式) "
                           + "margin?(200,离路线区距离) sheet_gap?(0) row_gap?(0) inner_margin_ratio?(0.05) "
                           + "offset_left? offset_right?(给了就顺手收窄断面显示宽度,解决格子装不下) "
                           + "draw_frames?(默认true,每页画闭合多段线图框,可直接喂 DWGTitleblockPlotter 批量打印) "
                           + "row_label?(默认true) label_height?(缺省图框高5%) label_gap? "
                           + "per_alignment_new_sheet?(默认true,仅sheets版式) alignments?[](缺省全部) x? y?(排版起点,缺省自动)",
                WritesDrawing = true,
                Run = RunNodeArrangeSectionSheets
            },
            ["restore_corridor_section_labels"] = new OpDef
            {
                Description = "宿主COM节点：对成果DWG执行 CORRIDORSECTIONLABELSCONV → ALL → C（选择集与动作在命令里写死，不是参数）",
                Parameters = "out(必需,另存路径) overwrite? visible?；实际执行入口 nodes/restore_corridor_section_labels/run.ps1，"
                           + "由 civil3dfactory.ps1 在 save_dwg 后调用；本入口只登记延期执行",
                WritesDrawing = false,
                Run = RunNodeRestoreCorridorSectionLabels
            },
            ["export_quantities"] = new OpDef
            {
                Description = "把算好的工程量导出 xlsx/csv（逐桩号累计/增量挖填 + 合计）",
                Parameters = "alignment(必需) outdir? format?(xlsx|csv|both,默认both)",
                WritesDrawing = false,
                Run = RunNodeExportQuantities
            },
            ["insert_title_blocks"] = new OpDef
            {
                Description = "按模型比例批量插图框块（A3@1:500 = 210×148.5 m 一张）；带 XData 标记可重复跑",
                Parameters = "block(必需,块名) from_dwg?(块库文件,图里没有该块时用) count?(默认1) cols?(默认1) x? y? "
                           + "paper?(A0..A4,默认A3) paper_w? paper_h?(mm,覆盖预设) scale?(缺省取图纸当前注释比例) "
                           + "block_scale?(缺省=scale/1000) gap_x? gap_y? layer? attributes?{标签:值}(值里 {n} 替换为序号) "
                           + "at?[{x,y,attributes?}](给定就逐点插并忽略网格参数；插入点=块外包左下角，"
                           + "喂 arrange_section_sheets 的 sheets[].origin_x/y 正好贴齐定位框；逐点 attributes 覆盖全局同名项) "
                           + "erase_same_block?(默认true，重跑前把模型空间同名块引用全清掉——XData 标记存盘会丢，只靠它会越插越摞) "
                           + "width_factors?{标签:因子}(长文字塞窄格：按标签把属性文字压扁) "
                           + "attsync?(默认true：插完对本块跑 ATTSYNC 同步属性位置/格式回块定义，再补一遍 width_factors)",
                WritesDrawing = true,
                Run = RunNodeInsertTitleBlocks
            },
            ["create_layout_sheet"] = new OpDef
            {
                Description = "创建布局图纸：图框放布局空间，视口按模型窗口定位，可固定比例并选择是否锁定",
                Parameters = "layout?(默认C3DF-A3) block(必需) from_dwg?(图中没有块定义时用) "
                           + "model_window?{minx,miny,maxx,maxy} boundary_handle? boundary_layer? paper?(默认A3) "
                           + "viewport?{x,y,width,height,scale?,locked?,layer?(默认C3DF-VPORT-NOPLOT不打印，"
                           + "要打印边框就给可打印层),freeze_layers?[]} frame_x? frame_y? frame_scale?(默认1) "
                           + "frame_layer?(默认0) attributes?{标签:值} width_factors?{标签:因子}(长图名塞窄格) "
                           + "extra_viewports?[{x,y,width,height,scale(必需),center_x?,center_y?,locked?,layer?,"
                           + "freeze_layers?[]}](同框附加小视口，如分幅图的平面索引钥匙图；缺中心沿用主窗中心) "
                           + "凡新建视口都先全层解冻再按 freeze_layers 冻——洗掉源图「新视口中冻结」层状态"
                           + "（那状态 DXF 解析读不到，会让整层内容在成图里凭空消失） "
                           + "clear?(默认true=重跑先清空本布局；false=在本布局里叠加一张，"
                           + "配合 frame_x/viewport.x 逐张平移就能把多幅分平面图横排进同一个布局)",
                WritesDrawing = true,
                Run = CreateLayoutSheet
            },
            ["create_sheet_region"] = new OpDef
            {
                Description = "在模型空间建立不打印的成图材料框；框尺寸由纸张和出图比例换算",
                Parameters = "x y(左下角；或给 alignment 自动按路线起点定位) paper?(默认A3) scale?(默认500) "
                           + "alignment? offset_x?(默认-100) offset_y?(默认-1600) "
                           + "name?(默认SHEET-01) layer?(默认C3DF-SHEET-REGION-NOPLOT) "
                           + "count?(默认1) pitch_x?(默认0) pitch_y?(默认0；批量时名称自动加-01/-02)",
                WritesDrawing = true,
                Run = RunNodeCreateSheetRegion
            },
            ["create_plan_frames_from_alignment"] = new OpDef
            {
                Description = "沿Civil路线生成连续水平分平面图框；固定世界坐标方向，不建立图纸集",
                Parameters = "alignment(必需) paper?(默认A3) scale?(默认5000) "
                           + "viewport_width_mm?(默认350) viewport_height_mm?(默认267) "
                           + "edge_margin_mm?(默认10) sample_step?(默认10m) overlap_ratio?(默认0.10) "
                           + "start_station? end_station? max_frames?(0=全部) frame_prefix? layer? replace_existing?",
                WritesDrawing = true,
                Run = RunNodeCreatePlanFramesFromAlignment
            },
            ["create_plan_layouts_from_frames"] = new OpDef
            {
                Description = "把水平分幅框批量转成当前DWG普通布局；创建锁定的0旋转视口并插入属性图框",
                Parameters = "block(必需) from_dwg? paper?(默认A3) scale?(默认5000) "
                           + "frame_layer? layout_prefix? max_layouts?(0=全部) viewport?{} "
                           + "frame_x? frame_y? frame_scale? frame_block_layer? attributes?{}",
                WritesDrawing = true,
                Run = RunNodeCreatePlanLayoutsFromFrames
            },
            ["compose_layout_sheet"] = new OpDef
            {
                Description = "创建实体化布局图纸：把当前/外部 DWG 模型空间实体缩放复制到布局空间，再套整张图框 DWG",
                Parameters = "layout?(默认C3DF-A3-实体) paper?(默认A3) clear?(默认true) "
                           + "sources?[{dwg?(缺省当前图),source_window?{minx,miny,maxx,maxy},crossing?(默认false),"
                           + "exclude_layers?[],paper_scale?(固定模型→纸空间倍率),target?{x,y,width,height},rotation?}] "
                           + "frame{block,from_dwg,x?,y?,scale?,attributes?{}}(必需) "
                           + "notes?[字符串] notes_x? notes_y? notes_width? notes_height?",
                WritesDrawing = true,
                Run = ComposeLayoutSheet
            },
            ["entity_stats"] = new OpDef
            {
                Description = "模型空间实体统计（按类型或图层分组），查\"这张图上到底是些什么\"",
                Parameters = "by?(type|layer,默认type) top?(默认30)",
                WritesDrawing = false,
                Run = EntityStats
            },
            ["set_scale"] = new OpDef
            {
                Description = "设模型空间比例：注释比例 CANNOSCALE + Civil 图形比例（图形设置→单位和比例，断面/纵断面标注字号看的是它）；图里没有这档就现建。回执带 civil_drawing_scale_before/after",
                Parameters = "scale?(纸1:图N 的 N；米制 1:500 传 0.5，传 500 会建成 1:500000) name?(比例名，如 \"1：500\" 注意全角/半角要跟图里那档一致) "
                           + "drawing_scale?(Civil 图形比例，缺省=注释比例同款比值 DrawingUnits/PaperUnits；项目A实测米制 0.5→注记 1:500、500→1:500000 且字号×1000)",
                WritesDrawing = true,
                Run = SetScale
            },
            ["set_model_view"] = new OpDef
            {
                Description = "设置并保存模型空间初始视图窗口，避免大批Civil对象图纸打开时立即绘制全图",
                Parameters = "minx miny maxx maxy(必需)",
                WritesDrawing = true,
                Run = SetModelView
            },
            ["export_to_autocad"] = new OpDef
            {
                Description = "把 Civil 3D 对象炸成纯 CAD 实体、另存新文件（-EXPORTTOAUTOCAD），原图不动",
                Parameters = "out(必需,绝对路径) version?(默认2018) overwrite?(默认false) "
                           + "explode_classes?([]DXF类名,导出前在内存里先把这些类就地炸成普通图元——"
                           + "断面 QTO 体积表 AECC_SECTION_VIEW_QUANTITY_TAKEOFF_TABLE 不炸会让导出 eLockViolation 流产；"
                           + "配合先 save_dwg 后导出，底稿保住活表)",
                WritesDrawing = false,
                Run = RunNodeExportToAutocad
            },
            ["annotate_grid_elevations"] = new OpDef
            {
                Description = "网格角点高程标注：边界图层上每条闭合多段线内打方格网，交叉点标设计/现有/差值三值",
                Parameters = "boundary_layer(必需,边界所在图层) design_surface(必需) existing_surface(必需) "
                           + "spacing?(默认50) text_height?(默认2.5) decimals?(默认2) offset_factor?(默认0.4) "
                           + "closed_only?(默认true) draw_grid?(默认true) clear_existing?(默认true,只删本节点XData对象) "
                           + "grid_layer?/design_layer?/exist_layer?/diff_layer?(默认 C3DF-网格/C3DF-DesignElevation/C3DF-现有高程/C3DF-差值)",
                WritesDrawing = true,
                Run = RunNodeAnnotateGridElevations
            },
            ["annotate_closed_polyline_areas"] = new OpDef
            {
                Description = "闭合多段线面积标注与 Excel 汇总：支持按句柄修正面积、重合标签自动错位和安全重跑",
                Parameters = "source_layer?(默认全部图层) text_height?(默认2.5) decimals?(默认2) "
                           + "annotation_layer?(默认C3DF-面积标注) color_index?(默认1) prefix? suffix?(默认 m²) "
                           + "include_zero?(默认true) clear_existing?(默认true) "
                           + "area_overrides?({句柄:面积}) label_offsets?({句柄:[dx,dy]}) "
                           + "export_excel?(默认true) outdir? excel_out_path? excel_format?(xlsx|csv|both)",
                WritesDrawing = true,
                Run = RunNodeAnnotateClosedPolylineAreas
            },
            ["calculate_surface_volume"] = new OpDef
            {
                Description = "曲面体积计算与疏浚统计（两曲面全范围 GetVolumeProperties；按闭合边界分块算量走 bounded_volumes。"
                            + "boundary_polyline 假参数 2026-08-20 已裁——它从不参与计算，收到即报错）",
                Parameters = "base_surface(必需) comparison_surface(必需) cut_factor? fill_factor? export_excel? excel_out_path? draw_dwg_table? table_insertion_point?",
                WritesDrawing = true,
                Run = RunNodeCalculateSurfaceVolume
            },
            ["survey_dredge_regions"] = new OpDef
            {
                Description = "疏浚区边界盘点与参数试算（只读）：边界图层上每条闭合多段线报句柄/面积/周长/形心/绕向/"
                            + "区内文字(自动认区名)/边界处原地形高程；给了 bottom_elev 还按 create_dredge_grading 同一个锥面模型"
                            + "网格试算挖填方——不建面不改图，先定参数再建面。放坡链路第一步",
                Parameters = "boundary_layer(必需,边界所在图层) boundaries?([]句柄白名单,给了就只盘这几条) "
                           + "surface?(原地形曲面名,给了才报高程与试算) bottom_elev?(给了才试算方量) "
                           + "slope_ratio_m?(默认5,1:m 的 m) regions?(同 create_dredge_grading,逐区覆盖;两个节点共用同一套合并口径) "
                           + "start_surface?(起坡高程取这个曲面,如已设计的通道面;采不到退回原地形) "
                           + "start_elev?(固定起坡高程) start_elev_cap?(起坡高程上限=min(地形,cap)) "
                           + "interface_layers?([]分界线图层,边界离这些线近的段压到 interface_start_elev) "
                           + "interface_tolerance?(默认2,边界点到分界线的距离容差) interface_start_elev?(缺省=bottom_elev,即该段不放坡) "
                           + "label_layer?(区名文字图层,缺省=所有非 C3DF-* 图层的文字) sample_step?(默认2,边界采样步长) "
                           + "grid_step?(默认5,试算网格) export_excel?(默认false) outdir? excel_out_path? excel_format?(xlsx|csv|both)",
                WritesDrawing = false,
                Run = RunNodeSurveyDredgeRegions
            },
            ["create_dredge_grading"] = new OpDef
            {
                Description = "疏浚放坡设计面（闭合边界→底高程+1:m 放坡）：边界多段线是**放坡起点线(上口线)**，逐区把范围挖到 "
                            + "bottom_elev、从边界按 1:m 向内下放坡建 TIN 设计面，另画坡顶线/坡脚线。"
                            + "用到边界的距离场(z=max(底, 起坡高程−距离/m))，凹角窄条不自交；放坡带内密采、平底稀采。"
                            + "全局参数 + regions 逐区覆盖 = 参数模型（不同区不同坡比就在这里给）",
                Parameters = "boundary_layer(必需) surface(必需,原地形曲面名) bottom_elev(必需,设计底高程) "
                           + "slope_ratio_m(必需,1:m 的 m) boundaries?([]句柄白名单) "
                           + "start_surface?(起坡高程取这个曲面,如已设计的通道面;采不到退回原地形) "
                           + "start_elev?(整圈固定起坡高程) start_elev_cap?(起坡高程上限=min(地形,cap)) "
                           + "interface_layers?([]分界线图层：与已挖通道相接的那几段边界，起坡高程压到 interface_start_elev) "
                           + "interface_tolerance?(默认2) interface_start_elev?(缺省=bottom_elev,即该段不放坡,坡由通道自己出) "
                           + "regions?([{id?,handle?,label?,bottom_elev?,slope_ratio_m?,start_elev?,start_elev_cap?,interface_start_elev?,z_segments?([{s0,s1,mode(现状|上限|定值|压底),value?}] 环弧长逐段顶高程,命中即权威盖过分界线口径),channel?,note?}] 逐区覆盖) "
                           + "label_layer?(区名文字图层,缺省=所有非 C3DF-* 图层的文字) sample_step?(默认2) slope_step?(默认2,放坡带网格) "
                           + "flat_step?(默认20,平底网格) surface_prefix?(默认「疏浚设计-」) surface_layer?(默认C3DF-DREDGE-SURFACE) "
                           + "crest_layer?(默认C3DF-DREDGE-TOP) toe_layer?(默认C3DF-DREDGE-TOE) draw_crest_line?(默认true) "
                           + "draw_toe_line?(默认true) clear_existing?(默认true,删 surface_prefix 前缀的曲面与带 C3DF_DREDGE XData 的线)",
                WritesDrawing = true,
                Run = RunNodeCreateDredgeGrading
            },
            ["merge_post_dredge_surface"] = new OpDef
            {
                Description = "工后曲面合成（设计∧现状取低）：疏浚只挖不填——逐点 z=min(设计面,现状地形) 建真实工后 TIN，"
                            + "低于设计高程的现状原样保留。临时高差 TIN 在挖侧 ε 处提零差等高线当折痕断裂线，"
                            + "同时落图 crease_layer 当挖区边界线；范围=设计面轮廓（外边界裁剪，多环时最大环当外圈其余当洞）。"
                            + "verify 默认开：现状 vs 工后 GetVolumeProperties，min() 恒不高于现状 ⇒ 填方必须≈0，"
                            + "残余填方 >0.5% 挖方就告警——安静地成功=没成功",
                Parameters = "design_surface(必需,设计曲面名) existing_surface(必需,现状地形曲面名) "
                           + "out_surface?(默认「工后-设计面名」;幂等重跑同名覆盖) surface_layer?(默认C3DF-POST-SURFACE) "
                           + "crease_layer?(默认C3DF-POST-ZERO-LINE) draw_crease_lines?(默认true,零差线落图当挖区边界线) "
                           + "min_crease_length?(默认1,更短的零差碎段丢弃) contour_epsilon?(默认0.001,零差线实际提在挖侧1mm处) "
                           + "border_step?(默认2,设计面轮廓展环步长) edge_step?(默认2,沿两面 TIN 棱细分采样的步长,0=关;"
                           + "面内是平面怎么三角化都精确、误差只出在跨棱处,顺棱采样比盲网格贴合) "
                           + "clear_existing?(默认true,清同名工后面与本节点零差线) "
                           + "verify?(默认true,现状vs工后+现状vs设计两组量对照自检)",
                WritesDrawing = true,
                Run = RunNodeMergePostDredgeSurface
            },
            ["parcel_grading_slope"] = new OpDef
            {
                Description = "地块向内/向外自动放坡",
                Parameters = "parcel_boundary(必需) target_surface? direction? target_type? target_value? cut_slope? fill_slope? bench_height? bench_width? draw_comb_lines? comb_spacing?",
                WritesDrawing = true,
                Run = RunNodeParcelGradingSlope
            },
            ["offset_cone_contours"] = new OpDef
            {
                Description = "偏移圆台：闭合边界批量偏移生成等高线（与 products\\waterbox 的 C3DF-OffsetCone/YT 同一算法核心）。"
                            + "默认内偏（岛收顶／坑收底）；outward=true 改外偏——边界当顶高程线往外摊到 target_z，岛屿外放坡就是这个",
                Parameters = "target_z(必需,目标设计高程) slope_n(必需,坡比1:n的n) step_dz?(高程间隔,默认0.5) "
                           + "outward?(默认false=内偏;true=外偏,边界不动往外放坡) "
                           + "fillet_r?(圆角半径,默认2·n·dz,0=不平滑) boundaries?[句柄数组] layer?(图层名,与boundaries二选一)",
                WritesDrawing = true,
                Run = RunNodeOffsetConeContours
            },
            ["erase_entities"] = new OpDef
            {
                Description = "按句柄删实体。删前把每个对象长什么样（类型/图层/高程/顶点/长度/面积）报出来，"
                            + "删完清单留在结果里；dry_run:true 只报不删。只按句柄，不开按图层批量删的口子",
                Parameters = "handles(必需,[]句柄数组) dry_run?(默认false)",
                WritesDrawing = true,
                Run = RunNodeEraseEntities
            },
            ["bounded_volumes"] = new OpDef
            {
                Description = "逐条闭合边界报体积曲面的挖填方（只读）：走 Surface.GetBoundedVolumes，"
                            + "就是体积面板里「加一条统计边界」拿到的那组数。"
                            + "⚠ 别拿 calculate_surface_volume 干这活——那个走 GetVolumeProperties 拿整个曲面的量，不认边界",
                Parameters = "volume_surface(已有体积曲面名) 或 base_surface+comparison_surface(没有就现建) "
                           + "layer?(边界所在图层) handles?([]句柄白名单)，二者至少给一个 "
                           + "names?({句柄:名称}) sample_step?(默认1,边界展成点环的步长,弧段按真实弧长) "
                           + "cut_factor?/fill_factor?(默认1,土方系数如 1.06) datum_elevation?(给了走带基准高程的重载) "
                           + "export_excel?(默认false) outdir? excel_format?(xlsx|csv|both,默认xlsx)",
                WritesDrawing = false,
                Run = RunNodeBoundedVolumes
            },
            ["grid_earthwork_balance"] = new OpDef
            {
                Description = "网格土方平衡：体积曲面上按格心撒格出逐格挖填 → 格间运输问题（精确最小 方量×运距）"
                            + "→ 内部调配量/加权运距/运距分档直方图，图内画挖填色块+聚合调配箭头（C3DF-平衡-* 四图层，幂等清旧）。"
                            + "量的真值走 GetBoundedVolumes 并做闭合差断言后配平——格距只影响运距分辨率不影响量。"
                            + "挖填差额挂虚拟节点（不进直方图不画箭头），报成 缺口借方/富余弃方。"
                            + "与 products\\waterbox 的 C3DF-GridBalance/PH 同一算法核心（GridBalanceCore.cs）",
                Parameters = "volume_surface(已有体积曲面名) 或 base_surface+comparison_surface(没有就现建) "
                           + "layer?(边界图层) handles?([]句柄白名单)，二者至少给一个 names?({句柄:名称}) "
                           + "step?(统计格距,默认5) solver_max_nodes?(求解节点上限,默认900,超了自动聚粗) "
                           + "bands?([]运距分档界m,默认[30,50,100,200,300,500]) volume_factor?(报表系数如1.06,默认1) "
                           + "closure_warn_pct?(默认2) closure_fail_pct?(默认10) sample_step?(边界展环步长,默认1) "
                           + "draw_cells?(默认true) draw_arrows?(默认true) max_arrows?(默认60) clear_previous?(默认true) "
                           + "export_excel?(默认true) outdir? excel_format?(xlsx|csv|both,默认xlsx)",
                WritesDrawing = true,
                Run = RunNodeGridEarthworkBalance
            },
            ["make_parcels"] = new OpDef
            {
                Description = "成田：范围线+圆滑中心线 → 通道带布尔 → 台田闭合边界（自动尖角甄别归圆）。"
                            + "多外圈自动分组（包含深度判外圈/岛洞，中心线按中点归组）；中心线端头贴边自动外延捅穿范围线；"
                            + "任一条成带失败整体报错不画。与 products\\waterbox 的 C3DF-MakeParcels/CT 同一算法核心（MakeParcelsCore.cs）",
                Parameters = "boundary_layer? boundary_handles?([]) 二者至少一（闭合/首尾重合的多段线） "
                           + "centerline_layer? centerline_handles?([]) 二者至少一（开放多段线） "
                           + "half_width?(通道半宽,默认15) fillet_r?(台田角半径,默认20) min_defl_deg?(归圆阈值角,默认25) "
                           + "min_area?(小台田报警面积,默认500,只报不删) out_layer?(默认 台田边界) "
                           + "channel_layer?(水道范围闭合环层,默认 水道范围,可HATCH) clear_previous?(默认true,两层幂等清旧)",
                WritesDrawing = true,
                Run = RunNodeMakeParcels
            },
            ["set_polyline_elevation"] = new OpDef
            {
                Description = "给多段线设高程（Z）：平面上画好的设计线常整层躺在 0 高程，进 TIN 或放坡前先抬到设计高程。只改 Elevation，不动几何图层",
                Parameters = "elevation?(一刀切高程) elevation_by_handle?({句柄:高程},逐条给,优先于 elevation) "
                           + "layer?(整层设) handles?([]句柄白名单)；圈定对象三选一，高程二选一",
                WritesDrawing = true,
                Run = RunNodeSetPolylineElevation
            },
            ["automate_2d_cross_sections"] = new OpDef
            {
                Description = "遍历模型空间既有闭合多段线批量打 Hatch（按图层名含 FILL/CONC 分挖填/结构）并出六行底栏表。"
                            + "⚠ 占位实现，不做坐标标定：桩号按序号×50 生成、设计高程按包围盒底+5、挖深写死 5.0、"
                            + "面积直接取多段线 Area、interaction_mode 读了不用——只能当画图辅助，不能用于算量。"
                            + "测量断面要算量走 extract_measured_sections → generate_demolition_design_lines → compute_embankment_demolition",
                Parameters = "interaction_mode?(只回显进汇报,不影响行为) hatch_rules?({cut,fill,concrete}图案名) "
                           + "draw_bottom_table? table_style?(读了不用) annotation_text_height?(读了不用)",
                WritesDrawing = true,
                Run = RunNodeAutomate2DCrossSections
            },
            ["test_twotier_channel"] = new OpDef
            {
                Description = "二级边坡试跑算子（包含主槽、一级边坡、马道/平台、二级边坡及衬砌）",
                Parameters = "center_x? center_y? bottom_width? h1? m1? bench_width? bench_slope? h2? m2? lining_thickness? layer?",
                WritesDrawing = true,
                Run = RunTestTwoTierChannel
            },
            ["create_twotier_bench_corridor"] = new OpDef
            {
                Description = "方案 B 二级边坡马道 C# 原生子装配与 3D Corridor 走廊流水线",
                Parameters = "bottom_width? h1? m1? bench_width? bench_slope? h2? m2? lining_thickness? alignment? surface? left_pkt? right_pkt? assembly? corridor?",
                WritesDrawing = true,
                Run = RunNodeCreateTwoTierBenchCorridor
            },
            ["create_3d_box"] = new OpDef
            {
                Description = "在 AutoCAD/Civil 3D 中创建 3D 实心长方体 (Solid3d Box)",
                Parameters = "x?(长,默认10) y?(宽,默认10) z?(高,默认5) cx?(默认0) cy?(默认0) cz?(默认0) layer?(默认C3DF-BOX-3D)",
                WritesDrawing = true,
                Run = RunCreate3DBox
            },
            ["create_3d_sphere"] = new OpDef
            {
                Description = "在 AutoCAD/Civil 3D 中创建 3D 实心球体 (Solid3d Sphere)",
                Parameters = "radius?(半径,默认3.0) cx?(默认0) cy?(默认0) cz?(默认8) layer?(默认C3DF-SPHERE-3D)",
                WritesDrawing = true,
                Run = RunCreate3DSphere
            },
            ["layers_off"] = new OpDef
            {
                Description = "关掉指定图层（出图时挡视线的填充层用它，如 C-RIVR-HATC-CUT）",
                Parameters = "name?(单个) names?[](多个)",
                WritesDrawing = true,
                Run = LayersOff
            },
            ["layers_on"] = new OpDef
            {
                Description = "把所有关掉/冻结的图层打开（headless 打印出白纸时先跑它）",
                Parameters = "(无)",
                WritesDrawing = true,
                Run = LayersOn
            },
            ["save_dwg"] = new OpDef
            {
                Description = "把内存中的改动存盘。缺省另存新文件；写回宿主图纸须 apply:true（自动先备份）",
                Parameters = "out?(绝对路径) apply?(默认false) backup?(默认true) overwrite?(默认false)",
                WritesDrawing = true,
                Run = RunNodeSaveDwg
            },
            ["rebuild_corridor"] = new OpDef
            {
                Description = "重建走廊并对比重建前后的链接代码（诊断子装配在当前宿主里跑不跑）",
                Parameters = "name(必需)",
                WritesDrawing = true,
                Run = RebuildCorridor
            },
            ["add_volume_tables"] = new OpDef
            {
                Description = "给既有断面视图挂 Civil QTO 体积表（不重建视图）。⚠ AEC 表在 accore 里导不出纯CAD"
                            + "（-EXPORTTOAUTOCAD eLockViolation 流产），出图链改用 draw_section_volume_tables 自画表；"
                            + "本节点留作界面出图/清场用",
                Parameters = "alignment(必需) group?(缺省<路线>_采样线组) clear_all?(默认false,先删模型空间全部断面QTO表,链里第一条路线传一次) "
                           + "create?(默认true;false=只清场不建表) offset_x?(默认5) offset_y?(默认0)",
                WritesDrawing = true,
                Run = RunNodeAddVolumeTables
            },
            ["draw_section_volume_tables"] = new OpDef
            {
                Description = "自画断面体积表（纯CAD线+文字，三行：桩号跨列/表头/挖方数据）：按采样线桩号贴在每张断面视图右上角。"
                            + "面积取 QTOSectionalResult.AreaResult.CutArea（逐断面精确），体积取增量挖方，均为原始值不乘系数。"
                            + "target_dwg 可把表直接画进已导出的纯CAD成品图（当前 Civil 图只出数据与坐标，不改）；"
                            + "实体带 XData(C3DF_SVT) 认亲，重跑先清旧表。AEC QTO 表无头导不出纯CAD，出图链一律用本节点",
                Parameters = "alignment(必需) group?(缺省<路线>_采样线组) scale?(默认500,出图比例,mm尺寸×scale/1000=模型米) "
                           + "text_mm?(2.5) row_mm?(5) col1_mm?(项目列16) col2_mm?(面积列30) col3_mm?(挖方列26) "
                           + "offset_x_mm?(2) offset_y_mm?(0) clear?(默认true) layer?(默认C3DF-体积表) "
                           + "target_dwg?(绝对路径,表画进这张成品图并存盘;被占用则落 -被占用待替换 件) "
                           + "text_style?(缺省自动挑 -黑体) station_prefix?(默认\"桩号 \") "
                           + "header_item?/header_area?/header_volume?/row_label?(表头文字) "
                           + "limit?(只画前N张,出样片用) dry_run?(默认false,只报位置与数据不画)",
                WritesDrawing = true,
                Run = RunNodeDrawSectionVolumeTables
            },
            ["find_write_open"] = new OpDef
            {
                Description = "扫全库列出仍处于写打开状态的对象（类型+句柄）。eWasOpenForWrite 不说肇事者是谁，靠这个定位；"
                            + "也用来验证 SaveDwg 的 ReclaimWriteOpen 兜底是否真收干净了",
                Parameters = "(无)",
                WritesDrawing = false,
                Run = RunNodeFindWriteOpen
            },
            ["rebind_volume_tables"] = new OpDef
            {
                Description = "把绑定失效的体积表重绑到所属路线的现材质列表（compute_quantities 重算后旧 Guid 悬空、表格全 0 的病）；"
                            + "位置样式不动，只换 MaterialListGuid 并重选材质，归属按最近路线判定并报距离",
                Parameters = "(无)",
                WritesDrawing = true,
                Run = RunNodeRebindVolumeTables
            },
            ["attach_baseline_profile"] = new OpDef
            {
                Description = "给走廊基线重挂纵断面引用（SetAlignmentAndProfile）并 Rebuild：设计纵断面被删又补回后，"
                            + "区间/链接码都在但道路曲面死了、rebuild_corridor 救不活——就是这个病",
                Parameters = "corridor(必需) alignment?(只挂该路线的基线) profile?(剖面线名,缺省自动挑设计线 FG/Layout) rebuild?(默认true)",
                WritesDrawing = true,
                Run = RunNodeAttachBaselineProfile
            },
            ["list_profiles"] = new OpDef
            {
                Description = "逐路线盘点剖面线/纵断面图/采样线组：has_ground、has_design 用的是 create_profile_view 同一套识别规则，"
                            + "can_make_profile_view=false 的路线出纵断面必失败——出图前的体检表",
                Parameters = "alignment?(只看一条) skip_offset_alignments?(默认true,不列 Alignment - (n)-Left 这类偏移路线)",
                WritesDrawing = false,
                Run = RunNodeListProfiles
            },
            ["fix_self_intersections"] = new OpDef
            {
                Description = "检测并修复自相交多段线（去回环）：闭合线保面积大的环、开放线丢套索回环；"
                            + "丢弃量超 max_drop_ratio 就停手报「需人工确认」，不替用户毁设计意图。"
                            + "进 TIN / 偏移 / 算面积 / 打 Hatch 之前跑一遍",
                Parameters = "handles?[句柄数组] layer?(图层名,与 handles 二选一) max_drop_ratio?(默认0.25) report_only?(默认false,只查不改)",
                WritesDrawing = true,
                Run = RunNodeFixSelfIntersections
            },
            ["export_material_volumes"] = new OpDef
            {
                Description = "一键导出全部路线材质体积表到一本 xlsx：sheet1「汇总」（中线名称/长度/总体积=累计挖方）+ 一路线一 sheet（逐桩号累计/增量挖填）；无材质列表的路线跳过并报名单",
                Parameters = "out?(xlsx 绝对路径,缺省 outdir\\材质体积表汇总.xlsx) outdir?",
                WritesDrawing = false,
                Run = RunNodeExportMaterialVolumes
            },
            ["export_all_dwg_tables"] = new OpDef
            {
                Description = "导出 DWG 图纸中所有的表格（材质体积表、CAD Table 实体、Civil 3D 材质工程量）到 Excel",
                Parameters = "target_dwg? excel_out?",
                WritesDrawing = false,
                Run = ExportAllDwgTables
            },
            ["surface_stats"] = new OpDef
            {
                Description = "曲面到底有没有几何：顶点数、三角形数、高程范围（查\"算出来是 0\"的第一站）",
                Parameters = "name?(缺省列全部曲面)",
                WritesDrawing = false,
                Run = SurfaceStats
            },
            ["corridor_stats"] = new OpDef
            {
                Description = "走廊结构：基准线/区域/桩号范围/链接代码/道路曲面三角形数",
                Parameters = "name?(缺省列全部走廊)",
                WritesDrawing = false,
                Run = CorridorStats
            },
            ["corridor_targets"] = new OpDef
            {
                Description = "只读盘点走廊目标：每条基线每个区域的目标槽（曲面/偏移/高程）各指向什么对象；曲线类目标顺带沿线采样实测相对基线路线的偏移分布，判「等距偏移段」用",
                Parameters = "name?(缺省全部走廊) sample_step?(曲线偏移采样步长m,默认10) max_samples?(每条曲线最多采样点,默认300)",
                WritesDrawing = false,
                Run = CorridorTargets
            },
            ["create_feature_lines"] = new OpDef
            {
                Description = "线升级要素线(FeatureLine)：高程模式 const(定值)/surface(AssignElevationsFromSurface 快照,含中间点)/cap(快照后 clamp 到 min(地形,z)——接台田口径)/keep(保源z)；弧段按 densify_step 细分成折线（v1 口径）；props 建线即挂分类。设计真源=可拖拽分类要素线的工作法入口",
                Parameters = "items(必需,[{handle,name,z_mode(const|surface|cap|keep),z?(const的定值或cap的上限),surface?,densify_step?(默认10),layer?,erase_source?(默认true),props?{set,values}}])",
                WritesDrawing = true,
                Run = CreateFeatureLines
            },
            ["dredge_from_feature_lines"] = new OpDef
            {
                Description = "分类要素线→疏浚设计面：按〈疏浚要素〉分类（分区号/角色/高程模式[定值|跟地形|上限X]/坡比m/设计底高程）收线→端点串环（平面距离点对点校验，接头高程跳变是设计不是缝）→距离场放坡（逐线坡比；跟地形/上限线现采曲面）→清本区旧线→TIN+坡脚线。底高程优先级：参数>线上〈设计底高程〉字段(打架报错)>口门线最低点；地形缺省读线上〈地形曲面〉字段。与 create_dredge_grading 共用成面段",
                Parameters = "region?(分区号,按分类扫描) lines?([]句柄白名单,与region二选一) set?(分类集名,默认疏浚要素) surface?(地形) bottom_elev? name?(设计面名,缺省 疏浚设计-{region}) sample_step?(2) slope_step?(2) flat_step?(20) chain_tol?(0.1) draw_toe?(true) draw_crest?(false,上口线就是要素线本身) surface_layer? crest_layer? toe_layer?",
                WritesDrawing = true,
                Run = DredgeFromFeatureLines
            },
            ["rename_alignments"] = new OpDef
            {
                Description = "批量改路线名：对象与句柄不动，偏移/走廊/特性集等引用全保留；目标名已存在或源名找不到都报错不静默",
                Parameters = "items(必需,[{from,to}])",
                WritesDrawing = true,
                Run = RenameAlignments
            },
            ["property_sets"] = new OpDef
            {
                Description = "特性集(AEC PropertySet)无头读写：define 建/补定义（幂等），assign 挂对象+赋值（重跑=刷新；值 \"@area\"/\"@length\" 从几何现算），dump 读回核对。口径：特性集只放身份+设计意图，不放会过期的计算结果",
                Parameters = "define?{name,applies_to?[RXClass名],fields[{name,type(text|real|integer),description?,default?}]} set?(assign用的集名,缺省=define.name) assign?[{handle,values{字段:值}}] dump?{handles?[]|layer?}",
                WritesDrawing = true,
                Run = PropertySets
            },
            ["api"] = new OpDef
            {
                Description = "反射列出某个类型的真实方法签名（写新操作查 Civil API 用，别猜）",
                Parameters = "type(必需,类名或全名) member?(方法名过滤) statics_only?(默认false) max?(默认120)",
                WritesDrawing = false,
                Run = ApiSignatures
            },
            ["civil_env"] = new OpDef
            {
                Description = "查这张图的 Civil 3D 家底：能不能取到 CivilDocument、有哪些装配/走廊/工程量准则/各类样式",
                Parameters = "max?(每类最多列几个，默认40)",
                WritesDrawing = false,
                Run = CivilEnv
            },
            ["list_styles"] = new OpDef
            {
                Description = "列出图中**全部**样式：反射遍历整棵 CivilDocument.Styles 树，不手写类别名",
                Parameters = "filter?(名称子串过滤,如 \"@\") max?(每类最多列几个,默认500) empty?(是否输出空类别,默认false) depth?(最大递归深度,默认6)",
                WritesDrawing = false,
                Run = ListStyles
            },
            ["profile_label_sets_dump"] = new OpDef
            {
                Description = "列出纵断面标签集的每个标签条目、类型和实际标签样式",
                Parameters = "name?(名称子串，缺省全部)",
                WritesDrawing = false,
                Run = ProfileLabelSetsDump
            },
            ["delete_styles"] = new OpDef
            {
                Description = "按「类别路径+样式名」批量删样式。缺省 dry_run 只报告不删；真删也只改内存，落盘要 save_dwg",
                Parameters = "items(必需,[{cat,name},…] cat 用 list_styles 的类别路径) dry_run?(默认true) passes?(默认3,被引用的样式等父级删掉后重试)",
                WritesDrawing = false,
                Run = DeleteStyles
            },
            ["style_display"] = new OpDef
            {
                Description = "看/改样式的「显示」设置（对应样式对话框 Display 页的图层/颜色/线型/线宽/可见）。不给 component 就列出全部分量及当前值",
                Parameters = "cat(必需,list_styles 的类别路径) name(必需) view?(Plan|Model|Section|Profile,默认Plan) component?(分量名,空格大小写随意) set?{color,layer,linetype,lineweight,linetype_scale,visible} dry_run?(默认true)",
                WritesDrawing = false,
                Run = StyleDisplay
            },
            ["list_layers"] = new OpDef
            {
                Description = "列出图层表：名称、颜色、线型、线宽、开关冻结锁定、是否被实体使用",
                Parameters = "used_only?(默认false) max?(默认2000)",
                WritesDrawing = false,
                Run = ListLayers
            },
            ["styles_audit"] = new OpDef
            {
                Description = "批量导出样式的显示设置：每个样式 × 每个视图 × 每个分量的颜色/图层/可见，供整体归一化用",
                Parameters = "filter?(样式名子串,如 \"@\") cats?[](只查这些类别路径) max?(默认2000)",
                WritesDrawing = false,
                Run = StylesAudit
            },
            ["layers_edit"] = new OpDef
            {
                Description = "建/改名/删图层。改名时**同步改写所有样式里的图层字符串**（Civil 的 DisplayStyle.Layer 是字符串，不同步就断链）",
                Parameters = "create?[{name,color?,linetype?}] rename?[{from,to}] delete?[名字数组] sync_styles?(默认true) skip_cats?[](默认跳过 AssemblyStyles，扫它会崩) dry_run?(默认true)",
                WritesDrawing = false,
                Run = LayersEdit
            },
            ["styles_normalize"] = new OpDef
            {
                Description = "批量归一化样式显示：颜色一律 ByLayer、把指定图层上的分量改派到目标图层",
                Parameters = "filter?(样式名子串) color_bylayer?(默认false) layer_moves?[{style,from,to}] dry_run?(默认true)",
                WritesDrawing = false,
                Run = StylesNormalize
            },
            ["rename_styles"] = new OpDef
            {
                Description = "批量改样式名",
                Parameters = "items(必需,[{cat,from,to}]) dry_run?(默认true)",
                WritesDrawing = false,
                Run = RenameStyles
            },
            ["import_styles"] = new OpDef
            {
                Description = "从另一个 DWG（样式库）导入样式到当前图。深克隆，依赖的子样式一并带过来",
                Parameters = "from(必需,库文件绝对路径) filter?(样式名子串,默认\"@\") items?[{cat,name}](给了就只导这些) cats?[](只在这些类别里找) mode?(ignore|replace,默认ignore=同名不覆盖) dry_run?(默认true)",
                WritesDrawing = false,
                Run = ImportStyles
            },
            ["code_set_dump"] = new OpDef
            {
                Description = "列出代码集样式的映射：每个代码挂了哪个样式/标签样式。断面上标注没出来时用它对账",
                Parameters = "name?(默认全部代码集) corridor?(给了就同时列出该走廊实际用到的代码，标出哪些没被覆盖)",
                WritesDrawing = false,
                Run = CodeSetDump
            },
            ["code_set_edit"] = new OpDef
            {
                Description = "给代码集补/改映射：某个代码挂哪个样式、哪个标签样式。断面上链接没样式没标注时用它接线",
                Parameters = "name(必需,代码集名) items(必需,[{code,style?,label_style?}]) dry_run?(默认true)",
                WritesDrawing = false,
                Run = CodeSetEdit
            },
            ["label_style_dump"] = new OpDef
            {
                Description = "解剖标签样式：有几个组件、每个组件的可见性/文字内容/图层/颜色。标注不显示时用它定位",
                Parameters = "name(必需,标签样式名，大小写敏感) cats?[](只在这些类别里找，默认全部标签类别)",
                WritesDrawing = false,
                Run = LabelStyleDump
            },
            ["dump_sections"] = new OpDef
            {
                Description = "按路线各取一个断面/断面图，逐属性 dump，用于新旧对比找差异",
                Parameters = "alignments(必需,路线名数组) which?(section|view|both,默认both)",
                WritesDrawing = false,
                Run = DumpSections
            },
            ["api_search"] = new OpDef
            {
                Description = "在 Civil/AutoCAD 程序集里按关键字搜类型和成员——不知道该用哪个 API 时先搜它",
                Parameters = "q(必需,关键字,可空格分隔多个) members?(是否连成员一起搜,默认true) max?(默认60)",
                WritesDrawing = false,
                Run = ApiSearch
            },
            ["snoop"] = new OpDef
            {
                Description = "反射列出对象的真实属性名和当前值（写代码前查 API 用，替代手动 Snoop）",
                Parameters = "type?(类型名含Alignment/Surface等,默认第一个匹配) name?(对象名) handle?(句柄) max?(默认80)",
                WritesDrawing = false,
                Run = Snoop
            },
        };

        public static JsonNode Execute(string op, JsonObject args, Document doc)
        {
            OpDef def;
            if (!Registry.TryGetValue(op, out def))
                throw new InvalidOperationException(
                    "未知操作 '" + op + "'。可用: " + string.Join(", ", Registry.Keys));
            return def.Run(args ?? new JsonObject(), doc);
        }

        // ===================== 操作实现 =====================

        static JsonNode DrawingInfo(JsonObject a, Document doc)
        {
            Database db = doc.Database;
            int alignments = 0, surfaces = 0, entities = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    entities++;
                    DBObject o = tr.GetObject(id, OpenMode.ForRead);
                    if (o is CivAlignment) alignments++;
                    else if (o is CivSurface) surfaces++;
                }
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                int layers = 0;
                foreach (ObjectId _ in lt) layers++;
                tr.Commit();

                return new JsonObject
                {
                    ["file"] = SafeFile(db),
                    ["entities"] = entities,
                    ["alignments"] = alignments,
                    ["surfaces"] = surfaces,
                    ["layers"] = layers,
                    ["units"] = db.Insunits.ToString()
                };
            }
        }

        static JsonNode ListAlignments(JsonObject a, Document doc)
        {
            var arr = new JsonArray();
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var al = tr.GetObject(id, OpenMode.ForRead) as CivAlignment;
                    if (al == null) continue;
                    arr.Add(new JsonObject
                    {
                        ["name"] = al.Name,
                        ["layer"] = SafeLayer(al),
                        ["length"] = Round(al.Length, 3),
                        ["start_station"] = Round(al.StartingStation, 3),
                        ["end_station"] = Round(al.EndingStation, 3),
                        ["handle"] = al.Handle.ToString()
                    });
                }
                tr.Commit();
            }
            return arr;
        }

        static JsonNode ListSurfaces(JsonObject a, Document doc)
        {
            var arr = new JsonArray();
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var s = tr.GetObject(id, OpenMode.ForRead) as CivSurface;
                    if (s == null) continue;
                    var o = new JsonObject
                    {
                        ["name"] = s.Name,
                        ["type"] = s.GetType().Name,
                        ["layer"] = SafeLayer(s),
                        ["handle"] = s.Handle.ToString()
                    };
                    arr.Add(o);
                }
                tr.Commit();
            }
            return arr;
        }

        static JsonNode ExportStations(JsonObject a, Document doc)
        {
            double interval = GetDouble(a, "interval", 50.0);
            string format = GetString(a, "format", "both");
            string outdir = ResolveOutDir(a, doc);
            var wanted = WantedNames(a);

            var files = new JsonArray();
            var summary = new JsonArray();
            Database db = doc.Database;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var al = tr.GetObject(id, OpenMode.ForRead) as CivAlignment;
                    if (al == null) continue;
                    if (wanted != null && !wanted.Contains(al.Name)) continue;

                    var rows = new List<object[]>();
                    int i = 0;
                    foreach (double st in Stations(al.StartingStation, al.EndingStation, interval))
                    {
                        double e = 0, n = 0;
                        try { al.PointLocation(st, 0.0, ref e, ref n); } catch { continue; }
                        i++;
                        rows.Add(new object[] { i, Station(st), Round2(n), Round2(e) });
                    }

                    string baseName = string.Format("路线坐标_{0}_{1:0}m", Sanitize(al.Name), interval);
                    foreach (string p in Excel.Write(outdir, baseName, HeadersXY, rows, format))
                        files.Add(p);

                    summary.Add(new JsonObject
                    {
                        ["alignment"] = al.Name,
                        ["points"] = rows.Count,
                        ["start_station"] = Round(al.StartingStation, 3),
                        ["end_station"] = Round(al.EndingStation, 3)
                    });
                }
                tr.Commit();
            }

            if (summary.Count == 0)
                throw new InvalidOperationException("没有匹配的路线。用 list_alignments 先看名称。");

            return new JsonObject { ["exported"] = summary, ["files"] = files, ["outdir"] = outdir };
        }

        static JsonNode StationElevations(JsonObject a, Document doc)
        {
            string alName = GetString(a, "alignment", null);
            string sfName = GetString(a, "surface", null);
            if (string.IsNullOrEmpty(alName)) throw new InvalidOperationException("缺少参数 alignment。");
            if (string.IsNullOrEmpty(sfName)) throw new InvalidOperationException("缺少参数 surface。");
            double interval = GetDouble(a, "interval", 50.0);
            string format = GetString(a, "format", "both");
            string outdir = ResolveOutDir(a, doc);

            Database db = doc.Database;
            var rows = new List<object[]>();
            int hit = 0, miss = 0;
            string usedAl = null, usedSf = null;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivAlignment al = null;
                CivSurface sf = null;
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    DBObject o = tr.GetObject(id, OpenMode.ForRead);
                    if (al == null && o is CivAlignment && ((CivAlignment)o).Name == alName) al = (CivAlignment)o;
                    else if (sf == null && o is CivSurface && ((CivSurface)o).Name == sfName) sf = (CivSurface)o;
                    if (al != null && sf != null) break;
                }
                if (al == null) throw new InvalidOperationException("找不到路线 '" + alName + "'。");
                if (sf == null) throw new InvalidOperationException("找不到曲面 '" + sfName + "'。");
                usedAl = al.Name; usedSf = sf.Name;

                int i = 0;
                foreach (double st in Stations(al.StartingStation, al.EndingStation, interval))
                {
                    double e = 0, n = 0;
                    try { al.PointLocation(st, 0.0, ref e, ref n); } catch { continue; }
                    i++;
                    object elev;
                    try { elev = Math.Round(sf.FindElevationAtXY(e, n), 3); hit++; }
                    catch { elev = null; miss++; }   // 点落在曲面外
                    rows.Add(new object[] { i, Station(st), Round2(n), Round2(e), elev });
                }
                tr.Commit();
            }

            string baseName = string.Format("路线地面高程_{0}_{1}_{2:0}m",
                Sanitize(usedAl), Sanitize(usedSf), interval);
            var files = new JsonArray();
            foreach (string p in Excel.Write(outdir, baseName, HeadersXYZ, rows, format)) files.Add(p);

            return new JsonObject
            {
                ["alignment"] = usedAl,
                ["surface"] = usedSf,
                ["points"] = rows.Count,
                ["on_surface"] = hit,
                ["off_surface"] = miss,
                ["files"] = files,
                ["outdir"] = outdir
            };
        }

        static JsonNode ExportSurfaceGrid(JsonObject a, Document doc)
        {
            string sfName = GetString(a, "surface", null);
            string outPath = GetString(a, "out", null);
            if (string.IsNullOrEmpty(sfName)) throw new InvalidOperationException("缺少参数 surface。");
            if (string.IsNullOrEmpty(outPath)) throw new InvalidOperationException("缺少参数 out。");
            double minx = GetDouble(a, "minx", double.NaN), miny = GetDouble(a, "miny", double.NaN);
            double maxx = GetDouble(a, "maxx", double.NaN), maxy = GetDouble(a, "maxy", double.NaN);
            if (double.IsNaN(minx) || double.IsNaN(miny) || double.IsNaN(maxx) || double.IsNaN(maxy))
                throw new InvalidOperationException("缺少参数 minx/miny/maxx/maxy。");
            if (maxx <= minx || maxy <= miny) throw new InvalidOperationException("范围无效：max 必须大于 min。");
            double step = GetDouble(a, "step", 10.0);
            if (step <= 0) throw new InvalidOperationException("step 必须大于 0。");
            bool overwrite = GetBool(a, "overwrite", false);
            if (File.Exists(outPath) && !overwrite)
                throw new InvalidOperationException("输出已存在且未给 overwrite:true：" + outPath);

            Database db = doc.Database;
            long hit = 0, miss = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivSurface sf = null;
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var o = tr.GetObject(id, OpenMode.ForRead) as CivSurface;
                    if (o != null && o.Name == sfName) { sf = o; break; }
                }
                if (sf == null) throw new InvalidOperationException("找不到曲面 '" + sfName + "'。用 list_surfaces 先看名称。");

                using (var w = new StreamWriter(outPath, false, System.Text.Encoding.UTF8))
                {
                    w.WriteLine("x,y,z");
                    for (double y = miny; y <= maxy; y += step)
                        for (double x = minx; x <= maxx; x += step)
                        {
                            double z;
                            try { z = sf.FindElevationAtXY(x, y); }
                            catch { miss++; continue; }   // 点落在曲面外
                            hit++;
                            w.WriteLine(x.ToString("0.###", CultureInfo.InvariantCulture) + ","
                                      + y.ToString("0.###", CultureInfo.InvariantCulture) + ","
                                      + z.ToString("0.###", CultureInfo.InvariantCulture));
                        }
                }
                tr.Commit();
            }
            if (hit == 0) throw new InvalidOperationException("采样范围内没有落在曲面上的点——范围或曲面名可能不对。");
            return new JsonObject
            {
                ["surface"] = sfName,
                ["out"] = outPath,
                ["step"] = step,
                ["on_surface"] = hit,
                ["off_surface"] = miss
            };
        }

        // ---------- Civil 3D 家底探测 ----------
        // 关键问题：accoreconsole 里能不能拿到 CivilDocument。
        // 拿不到 → 只能读对象（v1 的做法）；拿得到 → 建路线/走廊/工程量这条链才走得通。
        // 两条路都试，哪条通留哪条，结果一并报出来。
        static JsonNode CivilEnv(JsonObject a, Document doc)
        {
            int max = (int)GetDouble(a, "max", 40);
            Database db = doc.Database;
            var res = new JsonObject();

            CivDoc civ = null;
            var access = new JsonObject();
            try
            {
                CivDoc c = CivApp.ActiveDocument;
                access["CivilApplication.ActiveDocument"] = c != null ? "ok" : "null";
                if (c != null) civ = c;
            }
            catch (System.Exception ex)
            { access["CivilApplication.ActiveDocument"] = ex.GetType().Name + ": " + Truncate(ex.Message, 120); }

            try
            {
                CivDoc c = CivDoc.GetCivilDocument(db);
                access["CivilDocument.GetCivilDocument(db)"] = c != null ? "ok" : "null";
                if (c != null && civ == null) civ = c;
            }
            catch (System.Exception ex)
            { access["CivilDocument.GetCivilDocument(db)"] = ex.GetType().Name + ": " + Truncate(ex.Message, 120); }

            res["civil_document"] = access;
            res["usable"] = civ != null;

            // 模型空间里的 Civil 对象（不依赖 CivilDocument，v1 一直用这条路）
            var assemblies = new JsonArray();
            var corridors = new JsonArray();
            var alignments = new JsonArray();
            var surfaces = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    DBObject o;
                    try { o = tr.GetObject(id, OpenMode.ForRead); } catch { continue; }
                    if (o is Autodesk.Civil.DatabaseServices.Assembly)
                    {
                        var asm = (Autodesk.Civil.DatabaseServices.Assembly)o;
                        // 装配 → 组(AssemblyGroup) → GetSubassemblyIds()
                        var subs = new JsonArray();
                        try
                        {
                            foreach (Autodesk.Civil.DatabaseServices.AssemblyGroup g in asm.Groups)
                                foreach (ObjectId sid in g.GetSubassemblyIds())
                                {
                                    var sa = tr.GetObject(sid, OpenMode.ForRead)
                                             as Autodesk.Civil.DatabaseServices.Subassembly;
                                    if (sa == null) continue;
                                    // Status 是关键：Subassembly Composer 部件按路径引用 .pkt，
                                    // 路径失效时 Status=FileNotFound，走廊照样能建但**一点几何都没有**
                                    subs.Add(new JsonObject
                                    {
                                        ["name"] = sa.Name,
                                        ["status"] = sa.StatusOf(),
                                        ["from_composer"] = sa.IsComposer()
                                    });
                                }
                        }
                        catch (System.Exception ex) { subs.Add("(读取失败: " + ex.GetType().Name + ")"); }
                        assemblies.Add(new JsonObject { ["name"] = asm.Name, ["subassemblies"] = subs });
                    }
                    else if (o is Autodesk.Civil.DatabaseServices.Corridor)
                        corridors.Add(((Autodesk.Civil.DatabaseServices.Corridor)o).Name);
                    else if (o is CivAlignment) alignments.Add(((CivAlignment)o).Name);
                    else if (o is CivSurface) surfaces.Add(((CivSurface)o).Name);
                }
                tr.Commit();
            }
            res["assemblies"] = assemblies;
            res["corridors"] = corridors;
            res["alignments"] = alignments;
            res["surfaces"] = surfaces;

            if (civ == null) return res;

            // 样式与准则：只有拿到 CivilDocument 才读得到
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var styles = new JsonObject();
                Action<string, Func<object>> take = (key, getter) =>
                {
                    try { styles[key] = StyleNames(tr, getter(), max); }
                    catch (System.Exception ex) { styles[key] = ex.GetType().Name + ": " + Truncate(ex.Message, 100); }
                };
                take("alignment_styles", () => civ.Styles.AlignmentStyles);
                take("profile_styles", () => civ.Styles.ProfileStyles);
                take("surface_styles", () => civ.Styles.SurfaceStyles);
                take("sample_line_styles", () => civ.Styles.SampleLineStyles);
                take("assembly_styles", () => civ.Styles.AssemblyStyles);
                take("qto_criteria", () => civ.Styles.QuantityTakeoffCriterias);
                take("alignment_label_sets", () => civ.Styles.LabelSetStyles.AlignmentLabelSetStyles);
                take("profile_label_sets", () => civ.Styles.LabelSetStyles.ProfileLabelSetStyles);
                res["styles"] = styles;

                try
                {
                    var sites = new JsonArray();
                    foreach (ObjectId sid in civ.GetSiteIds())
                    {
                        var s = tr.GetObject(sid, OpenMode.ForRead);
                        string nm = TryGetName(s);
                        if (nm != null) sites.Add(nm);
                    }
                    res["sites"] = sites;
                }
                catch (System.Exception ex) { res["sites"] = ex.GetType().Name + ": " + Truncate(ex.Message, 100); }

                tr.Commit();
            }
            return res;
        }

        // ---- list_styles：反射遍历整棵 Styles 树 ----
        //
        // 为什么不像 civil_env 那样手写类别名：Civil 3D 的 CivilDocument.Styles 是一棵嵌套树
        // （StylesRoot → LabelStyles → AlignmentLabelStyles → StationLabelStyles → …），
        // 手写一层就只能读到一层，漏掉的类别在结果里完全看不出来。反射遍历才能保证「全部」。
        //
        // 判据：属性值是 IEnumerable → 当作样式集合收名字；否则若类型在 Autodesk.Civil 命名空间
        // 下 → 当作分组继续往下走。用引用集合防环，depth 兜底。
        static JsonNode ListStyles(JsonObject a, Document doc)
        {
            int max = (int)GetDouble(a, "max", 500);
            int maxDepth = (int)GetDouble(a, "depth", 6);
            bool keepEmpty = GetBool(a, "empty", false);
            string filter = GetString(a, "filter", null);

            Database db = doc.Database;
            CivDoc civ = null;
            try { civ = CivDoc.GetCivilDocument(db); } catch { }
            if (civ == null) { try { civ = CivApp.ActiveDocument; } catch { } }
            if (civ == null) throw new InvalidOperationException("取不到 CivilDocument，读不了样式。");

            var styles = new JsonObject();
            var errors = new JsonObject();
            int totalCollections = 0, totalStyles = 0, matched = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var seen = new List<object>();
                var stack = new List<KeyValuePair<string, object>>();
                stack.Add(new KeyValuePair<string, object>("", civ.Styles));

                while (stack.Count > 0)
                {
                    var cur = stack[0]; stack.RemoveAt(0);
                    string path = cur.Key;
                    object node = cur.Value;
                    if (node == null) continue;
                    if (path.Length > 0 && path.Split('.').Length > maxDepth) continue;

                    bool dup = false;
                    foreach (object s in seen) if (ReferenceEquals(s, node)) { dup = true; break; }
                    if (dup) continue;
                    seen.Add(node);

                    var props = node.GetType().GetProperties(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    foreach (var p in props)
                    {
                        if (!p.CanRead) continue;
                        if (p.GetIndexParameters().Length > 0) continue;   // 索引器不是分类
                        string key = path.Length == 0 ? p.Name : path + "." + p.Name;

                        object val;
                        try { val = p.GetValue(node, null); }
                        catch (System.Exception ex)
                        {
                            errors[key] = ex.GetType().Name + ": "
                                        + Truncate(ex.InnerException != null ? ex.InnerException.Message : ex.Message, 100);
                            continue;
                        }
                        if (val == null) continue;

                        Type vt = val.GetType();
                        if (val is string || vt.IsPrimitive || vt.IsEnum) continue;

                        if (val is System.Collections.IEnumerable)
                        {
                            JsonArray names;
                            try { names = StyleNames(tr, val, max); }
                            catch (System.Exception ex)
                            { errors[key] = ex.GetType().Name + ": " + Truncate(ex.Message, 100); continue; }

                            totalCollections++;
                            totalStyles += names.Count;

                            if (filter != null)
                            {
                                var hit = new JsonArray();
                                foreach (var n in names)
                                {
                                    string s = n == null ? "" : n.ToString();
                                    if (s.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                                        hit.Add(s);
                                }
                                matched += hit.Count;
                                if (hit.Count > 0 || keepEmpty) styles[key] = hit;
                            }
                            else if (names.Count > 0 || keepEmpty) styles[key] = names;
                        }
                        else if (vt.FullName != null && vt.FullName.StartsWith("Autodesk.Civil"))
                        {
                            // 只往「分组」里钻，不钻「单个样式对象」：像 LabelStyles.DefaultLabelStyle
                            // 是一个具体样式，往下走会碰到一堆 .Overridden——它们对非覆盖项固定抛
                            // TargetInvocationException("This property is not overridable")，
                            // 白刷 30 多条假错误。
                            // 判据用类型名后缀 Root（StylesRoot / LabelStyleRoot / BandStyleRoot /
                            // TableStyleRoot …全部分组都是这个后缀）；TryGetName 试过，
                            // DefaultLabelStyle 在这里取不到名字，挡不住。
                            if (!vt.Name.EndsWith("Root")) continue;
                            stack.Add(new KeyValuePair<string, object>(key, val));
                        }
                    }
                }
                tr.Commit();
            }

            var res = new JsonObject
            {
                ["collections_found"] = totalCollections,
                ["styles_total"] = totalStyles
            };
            if (filter != null) { res["filter"] = filter; res["styles_matched"] = matched; }
            res["styles"] = styles;
            if (errors.Count > 0) res["unreadable"] = errors;
            return res;
        }

        // 按 list_styles 的类别路径（如 "LabelStyles.ProfileLabelStyles.MajorStationLabelStyles"）
        // 逐段取属性，拿到那个样式集合对象。取不到返回 null。
        static object ResolveStyleCollection(CivDoc civ, string path)
        {
            object node = civ.Styles;
            foreach (string seg in path.Split('.'))
            {
                if (node == null) return null;
                var p = node.GetType().GetProperty(seg,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (p == null || !p.CanRead) return null;
                try { node = p.GetValue(node, null); }
                catch { return null; }
            }
            return node;
        }

        // ---- delete_styles ----
        //
        // 只删「类别路径 + 名称」精确命中的样式。两条安全线：
        //   1. 缺省 dry_run=true，只解析报告不动手；
        //   2. 即使真删也只改内存，磁盘要靠 save_dwg，而 save_dwg 缺省另存新文件。
        // 被别的样式/对象引用的删不掉，Civil 会抛异常——照实记 error，不吞、不强删。
        // 多跑几遍（passes）：父级（如标签组）删掉后，原先被它引用的子样式才删得动。
        static JsonNode DeleteStyles(JsonObject a, Document doc)
        {
            var items = a["items"] as JsonArray;
            if (items == null || items.Count == 0)
                throw new InvalidOperationException("delete_styles 需要 items:[{cat,name},…]");
            bool dry = GetBool(a, "dry_run", true);
            int passes = (int)GetDouble(a, "passes", 3);

            Database db = doc.Database;
            CivDoc civ = null;
            try { civ = CivDoc.GetCivilDocument(db); } catch { }
            if (civ == null) throw new InvalidOperationException("取不到 CivilDocument。");

            // 目标清单 → (cat, name)
            var targets = new List<string[]>();
            foreach (JsonNode it in items)
            {
                var o = it as JsonObject;
                if (o == null) continue;
                string cat = o["cat"] != null ? o["cat"].ToString() : null;
                string nm = o["name"] != null ? o["name"].ToString() : null;
                if (cat != null && nm != null) targets.Add(new string[] { cat, nm });
            }

            var done = new JsonArray();
            var failed = new JsonArray();
            var notfound = new JsonArray();
            int deleted = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var pending = targets;
                for (int pass = 1; pass <= passes && pending.Count > 0; pass++)
                {
                    var stillPending = new List<string[]>();
                    var lastErr = new Dictionary<string, string>();

                    foreach (string[] t in pending)
                    {
                        object coll = ResolveStyleCollection(civ, t[0]);
                        var en = coll as System.Collections.IEnumerable;
                        if (en == null)
                        {
                            if (pass == passes)
                                notfound.Add(new JsonObject
                                { ["cat"] = t[0], ["name"] = t[1], ["why"] = "类别路径解析不到集合" });
                            continue;
                        }

                        ObjectId hit = ObjectId.Null;
                        foreach (object item in en)
                        {
                            if (!(item is ObjectId)) continue;
                            ObjectId oid = (ObjectId)item;
                            if (oid.IsErased) continue;
                            try
                            {
                                if (TryGetName(tr.GetObject(oid, OpenMode.ForRead)) == t[1])
                                { hit = oid; break; }
                            }
                            catch { }
                        }

                        if (hit.IsNull)
                        {
                            if (pass == passes)
                                notfound.Add(new JsonObject
                                { ["cat"] = t[0], ["name"] = t[1], ["why"] = "集合里没有这个名字" });
                            continue;
                        }

                        if (dry)
                        {
                            done.Add(new JsonObject
                            { ["cat"] = t[0], ["name"] = t[1], ["handle"] = hit.Handle.ToString() });
                            deleted++;
                            continue;
                        }

                        try
                        {
                            DBObject o = tr.GetObject(hit, OpenMode.ForWrite);
                            o.Erase();
                            done.Add(new JsonObject
                            { ["cat"] = t[0], ["name"] = t[1], ["pass"] = pass });
                            deleted++;
                        }
                        catch (System.Exception ex)
                        {
                            stillPending.Add(t);
                            lastErr[t[0] + "||" + t[1]] =
                                ex.GetType().Name + ": " + Truncate(ex.Message, 120);
                        }
                    }

                    if (pass == passes || stillPending.Count == 0)
                    {
                        foreach (string[] t in stillPending)
                            failed.Add(new JsonObject
                            {
                                ["cat"] = t[0],
                                ["name"] = t[1],
                                ["error"] = lastErr[t[0] + "||" + t[1]]
                            });
                    }
                    pending = stillPending;
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["dry_run"] = dry,
                ["requested"] = targets.Count,
                ["deleted"] = deleted,
                ["failed_count"] = failed.Count,
                ["notfound_count"] = notfound.Count,
                ["failed"] = failed,
                ["notfound"] = notfound,
                ["done"] = done
            };
        }

        // 遍历所有样式的所有显示分量，交给回调处理。
        // skipCats 默认含 AssemblyStyles：扫它会 AccessViolation 硬崩进程（.NET 捕不到）。
        static void ForEachDisplay(Database db, Transaction tr, CivDoc civ,
                                   List<string> skipCats, string nameFilter,
                                   Action<string, string, object, DBObject> visit)
        {
            var stack = new List<KeyValuePair<string, object>>();
            stack.Add(new KeyValuePair<string, object>("", civ.Styles));
            var seen = new List<object>();
            var collections = new List<KeyValuePair<string, object>>();

            while (stack.Count > 0)
            {
                var cur = stack[0]; stack.RemoveAt(0);
                if (cur.Value == null) continue;
                bool dup = false;
                foreach (object s in seen) if (ReferenceEquals(s, cur.Value)) { dup = true; break; }
                if (dup) continue;
                seen.Add(cur.Value);
                foreach (var p in cur.Value.GetType().GetProperties(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                    string key = cur.Key.Length == 0 ? p.Name : cur.Key + "." + p.Name;
                    object val;
                    try { val = p.GetValue(cur.Value, null); } catch { continue; }
                    if (val == null) continue;
                    Type vt = val.GetType();
                    if (val is string || vt.IsPrimitive || vt.IsEnum) continue;
                    if (val is System.Collections.IEnumerable)
                        collections.Add(new KeyValuePair<string, object>(key, val));
                    else if (vt.FullName != null && vt.FullName.StartsWith("Autodesk.Civil")
                             && vt.Name.EndsWith("Root"))
                        stack.Add(new KeyValuePair<string, object>(key, val));
                }
            }

            foreach (var kv in collections)
            {
                bool skip = false;
                foreach (string sc in skipCats)
                    if (kv.Key == sc || kv.Key.EndsWith("." + sc)) { skip = true; break; }
                if (skip) continue;
                var en = kv.Value as System.Collections.IEnumerable;
                if (en == null) continue;
                foreach (object item in en)
                {
                    if (!(item is ObjectId)) continue;
                    ObjectId oid = (ObjectId)item;
                    if (oid.IsErased) continue;
                    DBObject so;
                    try { so = tr.GetObject(oid, OpenMode.ForRead); } catch { continue; }
                    string nm = TryGetName(so);
                    if (nm == null) continue;
                    if (nameFilter != null &&
                        nm.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    foreach (var m in so.GetType().GetMethods(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    {
                        if (!m.Name.StartsWith("GetDisplayStyle")) continue;
                        var ps = m.GetParameters();
                        if (ps.Length != 1 || !ps[0].ParameterType.IsEnum) continue;
                        Type et = ps[0].ParameterType;
                        foreach (string cn in Enum.GetNames(et))
                        {
                            object ds;
                            try { ds = m.Invoke(so, new object[] { Enum.Parse(et, cn) }); }
                            catch { continue; }
                            if (ds == null) continue;
                            visit(kv.Key, nm, ds, so);
                        }
                    }
                }
            }
        }

        static List<string> SkipCats(JsonObject a)
        {
            var skip = new List<string>();
            var arr = a["skip_cats"] as JsonArray;
            if (arr == null) skip.Add("AssemblyStyles");
            else foreach (JsonNode n in arr) if (n != null) skip.Add(n.ToString());
            return skip;
        }

        static JsonNode LayersEdit(JsonObject a, Document doc)
        {
            bool dry = GetBool(a, "dry_run", true);
            bool sync = GetBool(a, "sync_styles", true);
            var skip = SkipCats(a);
            Database db = doc.Database;
            CivDoc civ = null;
            try { civ = CivDoc.GetCivilDocument(db); } catch { }

            var created = new JsonArray();
            var renamed = new JsonArray();
            var deleted = new JsonArray();
            var failed = new JsonArray();
            int styleRefsPatched = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId,
                    dry ? OpenMode.ForRead : OpenMode.ForWrite);

                // --- 建 ---
                var arr = a["create"] as JsonArray;
                if (arr != null)
                    foreach (JsonNode n in arr)
                    {
                        var o = n as JsonObject; if (o == null) continue;
                        string nm = o["name"].ToString();
                        if (lt.Has(nm)) { created.Add(nm + "（已存在，跳过）"); continue; }
                        if (dry) { created.Add(nm + "（预演）"); continue; }
                        var ltr = new LayerTableRecord { Name = nm };
                        if (o["color"] != null) ltr.Color = ParseColor(o["color"].ToString());
                        lt.Add(ltr);
                        tr.AddNewlyCreatedDBObject(ltr, true);
                        created.Add(nm);
                    }

                // --- 改名（含样式字符串同步）---
                var ren = a["rename"] as JsonArray;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (ren != null)
                    foreach (JsonNode n in ren)
                    {
                        var o = n as JsonObject; if (o == null) continue;
                        string from = o["from"].ToString(), to = o["to"].ToString();
                        if (!lt.Has(from))
                        { failed.Add(new JsonObject { ["op"] = "rename", ["name"] = from, ["why"] = "图层不存在" }); continue; }
                        if (lt.Has(to))
                        { failed.Add(new JsonObject { ["op"] = "rename", ["name"] = from, ["why"] = "目标名 " + to + " 已存在" }); continue; }
                        map[from] = to;
                        if (dry) { renamed.Add(from + " → " + to + "（预演）"); continue; }
                        try
                        {
                            var ltr = (LayerTableRecord)tr.GetObject(lt[from], OpenMode.ForWrite);
                            ltr.Name = to;
                            renamed.Add(from + " → " + to);
                        }
                        catch (System.Exception ex)
                        { failed.Add(new JsonObject { ["op"] = "rename", ["name"] = from, ["why"] = ex.GetType().Name + ": " + Truncate(ex.Message, 90) }); }
                    }

                // 样式里的图层字符串跟着改（不做这步就是断链）
                if (sync && map.Count > 0 && civ != null)
                {
                    ForEachDisplay(db, tr, civ, skip, null, delegate (string cat, string sname, object ds, DBObject so)
                    {
                        var p = ds.GetType().GetProperty("Layer");
                        if (p == null) return;
                        object v;
                        try { v = p.GetValue(ds, null); } catch { return; }
                        string cur = v as string;
                        if (cur == null || !map.ContainsKey(cur)) return;
                        styleRefsPatched++;
                        if (dry) return;
                        try { so.UpgradeOpen(); } catch { }
                        try { p.SetValue(ds, map[cur], null); } catch { }
                    });
                }

                // --- 删 ---
                var del = a["delete"] as JsonArray;
                if (del != null)
                    foreach (JsonNode n in del)
                    {
                        if (n == null) continue;
                        string nm = n.ToString();
                        if (!lt.Has(nm))
                        { failed.Add(new JsonObject { ["op"] = "delete", ["name"] = nm, ["why"] = "图层不存在" }); continue; }
                        if (dry) { deleted.Add(nm + "（预演）"); continue; }
                        try
                        {
                            var ltr = (LayerTableRecord)tr.GetObject(lt[nm], OpenMode.ForWrite);
                            ltr.Erase();
                            deleted.Add(nm);
                        }
                        catch (System.Exception ex)
                        { failed.Add(new JsonObject { ["op"] = "delete", ["name"] = nm, ["why"] = ex.GetType().Name + ": " + Truncate(ex.Message, 90) }); }
                    }

                tr.Commit();
            }

            return new JsonObject
            {
                ["dry_run"] = dry,
                ["created"] = created,
                ["renamed"] = renamed,
                ["deleted"] = deleted,
                ["style_refs_patched"] = styleRefsPatched,
                ["failed"] = failed
            };
        }

        static JsonNode StylesNormalize(JsonObject a, Document doc)
        {
            bool dry = GetBool(a, "dry_run", true);
            string filter = GetString(a, "filter", null);
            bool toByLayer = GetBool(a, "color_bylayer", false);
            var skip = SkipCats(a);

            // {style,from,to}：把某样式里 layer==from 的分量改派到 to
            var moves = new List<string[]>();
            var mv = a["layer_moves"] as JsonArray;
            if (mv != null)
                foreach (JsonNode n in mv)
                {
                    var o = n as JsonObject; if (o == null) continue;
                    moves.Add(new string[]{
                        o["style"] == null ? null : o["style"].ToString(),
                        o["from"] == null ? "0" : o["from"].ToString(),
                        o["to"] == null ? null : o["to"].ToString() });
                }

            Database db = doc.Database;
            CivDoc civ = null;
            try { civ = CivDoc.GetCivilDocument(db); } catch { }
            if (civ == null) throw new InvalidOperationException("取不到 CivilDocument。");

            int colorChanged = 0, layerChanged = 0, visited = 0;
            var byStyle = new JsonObject();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ForEachDisplay(db, tr, civ, skip, filter,
                delegate (string cat, string sname, object ds, DBObject so)
                {
                    visited++;
                    Type t = ds.GetType();

                    if (toByLayer)
                    {
                        var pc = t.GetProperty("Color");
                        if (pc != null)
                        {
                            object v = null;
                            try { v = pc.GetValue(ds, null); } catch { }
                            var col = v as Autodesk.AutoCAD.Colors.Color;
                            if (col != null && !col.IsByLayer)
                            {
                                colorChanged++;
                                Bump(byStyle, sname, "color");
                                if (!dry)
                                {
                                    try { so.UpgradeOpen(); } catch { }
                                    try { pc.SetValue(ds, ParseColor("ByLayer"), null); } catch { }
                                }
                            }
                        }
                    }

                    if (moves.Count > 0)
                    {
                        var pl = t.GetProperty("Layer");
                        if (pl != null)
                        {
                            object v = null;
                            try { v = pl.GetValue(ds, null); } catch { }
                            string cur = v as string;
                            if (cur != null)
                                foreach (string[] m in moves)
                                {
                                    if (m[2] == null) continue;
                                    if (m[0] != null && m[0] != sname) continue;
                                    if (cur != m[1]) continue;
                                    layerChanged++;
                                    Bump(byStyle, sname, "layer");
                                    if (!dry)
                                    {
                                        try { so.UpgradeOpen(); } catch { }
                                        try { pl.SetValue(ds, m[2], null); } catch { }
                                    }
                                    break;
                                }
                        }
                    }
                });
                tr.Commit();
            }

            return new JsonObject
            {
                ["dry_run"] = dry,
                ["filter"] = filter,
                ["components_visited"] = visited,
                ["color_set_bylayer"] = colorChanged,
                ["layer_reassigned"] = layerChanged,
                ["by_style"] = byStyle
            };
        }

        static void Bump(JsonObject o, string style, string kind)
        {
            var cell = o[style] as JsonObject;
            if (cell == null) { cell = new JsonObject(); o[style] = cell; }
            int n = cell[kind] != null ? (int)cell[kind] : 0;
            cell[kind] = n + 1;
        }

        static JsonNode RenameStyles(JsonObject a, Document doc)
        {
            bool dry = GetBool(a, "dry_run", true);
            var items = a["items"] as JsonArray;
            if (items == null || items.Count == 0)
                throw new InvalidOperationException("rename_styles 需要 items:[{cat,from,to}]");

            Database db = doc.Database;
            CivDoc civ = null;
            try { civ = CivDoc.GetCivilDocument(db); } catch { }
            if (civ == null) throw new InvalidOperationException("取不到 CivilDocument。");

            var done = new JsonArray();
            var failed = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (JsonNode n in items)
                {
                    var o = n as JsonObject; if (o == null) continue;
                    string cat = o["cat"].ToString(), from = o["from"].ToString(), to = o["to"].ToString();
                    object coll = ResolveStyleCollection(civ, cat);
                    var en = coll as System.Collections.IEnumerable;
                    if (en == null)
                    { failed.Add(new JsonObject { ["from"] = from, ["why"] = "类别路径解析不到: " + cat }); continue; }

                    DBObject hit = null;
                    foreach (object item in en)
                    {
                        if (!(item is ObjectId)) continue;
                        ObjectId oid = (ObjectId)item;
                        if (oid.IsErased) continue;
                        DBObject so;
                        try { so = tr.GetObject(oid, OpenMode.ForRead); } catch { continue; }
                        if (TryGetName(so) == from) { hit = so; break; }
                    }
                    if (hit == null)
                    { failed.Add(new JsonObject { ["from"] = from, ["why"] = "该类别里没有此样式" }); continue; }
                    if (dry) { done.Add(from + " → " + to + "（预演）"); continue; }

                    // Name 在派生层被 new 重声明且可能没 setter，逐层往基类找真有 set 的那个
                    bool ok = false; string err = null;
                    var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                              | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
                    for (Type t = hit.GetType(); t != null && !ok; t = t.BaseType)
                    {
                        var p = t.GetProperty("Name", flags);
                        if (p == null) continue;
                        var setter = p.GetSetMethod(true);
                        if (setter == null) continue;
                        try
                        {
                            hit.UpgradeOpen();
                            setter.Invoke(hit, new object[] { to });
                            ok = true;
                        }
                        catch (System.Exception ex)
                        { err = ex.GetType().Name + ": " + Truncate(ex.InnerException != null ? ex.InnerException.Message : ex.Message, 90); }
                    }
                    if (ok) done.Add(from + " → " + to);
                    else failed.Add(new JsonObject { ["from"] = from, ["why"] = err ?? "找不到可写的 Name 属性" });
                }
                tr.Commit();
            }
            return new JsonObject { ["dry_run"] = dry, ["done"] = done, ["failed"] = failed };
        }

        // ---- import_styles：从样式库 DWG 把样式搬过来 ----
        //
        // 做法：把库文件当侧数据库打开，按「类别路径 + 名称」找到源样式的 ObjectId，
        // 再 WblockCloneObjects 到目标图里**同一类别集合的宿主字典**。
        // 用深克隆的原因：Civil 样式互相引用（代码集→链接/标记/形状样式、断面图样式→标注栏集…），
        // 只搬一个壳过去会缺依赖。目标字典靠「取该集合里任一现有样式的 OwnerId」拿到——
        // 每个集合至少有一条 Standard，所以这条路稳。
        static JsonNode ImportStyles(JsonObject a, Document doc)
        {
            string from = GetString(a, "from", null);
            if (from == null) throw new InvalidOperationException("import_styles 需要 from（库文件路径）");
            if (!File.Exists(from)) throw new InvalidOperationException("找不到库文件: " + from);
            bool dry = GetBool(a, "dry_run", true);
            string filter = GetString(a, "filter", "@");
            string mode = GetString(a, "mode", "ignore");
            var wantItems = a["items"] as JsonArray;
            var onlyCats = a["cats"] as JsonArray;

            Database dstDb = doc.Database;
            CivDoc dstCiv = null;
            try { dstCiv = CivDoc.GetCivilDocument(dstDb); } catch { }
            if (dstCiv == null) throw new InvalidOperationException("目标图取不到 CivilDocument。");

            var planned = new JsonArray();
            var done = new JsonArray();
            var skipped = new JsonArray();
            var failed = new JsonArray();

            using (var srcDb = new Database(false, true))
            {
                srcDb.ReadDwgFile(from, FileOpenMode.OpenForReadAndAllShare, true, null);
                srcDb.CloseInput(true);

                CivDoc srcCiv = null;
                try { srcCiv = CivDoc.GetCivilDocument(srcDb); } catch { }
                if (srcCiv == null)
                    throw new InvalidOperationException("库文件取不到 CivilDocument（它是 Civil 图吗？）");

                // 枚举源库里所有样式集合（复用 Root 后缀判据）
                var srcCollections = new List<KeyValuePair<string, object>>();
                {
                    var stack = new List<KeyValuePair<string, object>>();
                    stack.Add(new KeyValuePair<string, object>("", srcCiv.Styles));
                    var seen = new List<object>();
                    while (stack.Count > 0)
                    {
                        var cur = stack[0]; stack.RemoveAt(0);
                        if (cur.Value == null) continue;
                        bool dup = false;
                        foreach (object s in seen) if (ReferenceEquals(s, cur.Value)) { dup = true; break; }
                        if (dup) continue;
                        seen.Add(cur.Value);
                        foreach (var p in cur.Value.GetType().GetProperties(
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        {
                            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                            string key = cur.Key.Length == 0 ? p.Name : cur.Key + "." + p.Name;
                            object val;
                            try { val = p.GetValue(cur.Value, null); } catch { continue; }
                            if (val == null) continue;
                            Type vt = val.GetType();
                            if (val is string || vt.IsPrimitive || vt.IsEnum) continue;
                            if (val is System.Collections.IEnumerable)
                                srcCollections.Add(new KeyValuePair<string, object>(key, val));
                            else if (vt.FullName != null && vt.FullName.StartsWith("Autodesk.Civil")
                                     && vt.Name.EndsWith("Root"))
                                stack.Add(new KeyValuePair<string, object>(key, val));
                        }
                    }
                }

                // 逐类别挑要导的样式
                var perCat = new Dictionary<string, List<KeyValuePair<ObjectId, string>>>();
                using (Transaction str = srcDb.TransactionManager.StartTransaction())
                {
                    foreach (var kv in srcCollections)
                    {
                        if (kv.Key == "AssemblyStyles") continue;   // 碰它会崩，且用不上
                        if (onlyCats != null)
                        {
                            bool want = false;
                            foreach (JsonNode c in onlyCats)
                                if (c != null && c.ToString() == kv.Key) { want = true; break; }
                            if (!want) continue;
                        }
                        var en = kv.Value as System.Collections.IEnumerable;
                        if (en == null) continue;
                        foreach (object item in en)
                        {
                            if (!(item is ObjectId)) continue;
                            ObjectId oid = (ObjectId)item;
                            if (oid.IsErased) continue;
                            string nm;
                            try { nm = TryGetName(str.GetObject(oid, OpenMode.ForRead)); } catch { continue; }
                            if (nm == null) continue;

                            bool take;
                            if (wantItems != null)
                            {
                                take = false;
                                foreach (JsonNode n in wantItems)
                                {
                                    var o = n as JsonObject; if (o == null) continue;
                                    if (o["cat"].ToString() == kv.Key && o["name"].ToString() == nm)
                                    { take = true; break; }
                                }
                            }
                            else
                                take = filter == null ||
                                       nm.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

                            if (!take) continue;
                            if (!perCat.ContainsKey(kv.Key))
                                perCat[kv.Key] = new List<KeyValuePair<ObjectId, string>>();
                            perCat[kv.Key].Add(new KeyValuePair<ObjectId, string>(oid, nm));
                        }
                    }
                    str.Commit();
                }

                // 目标图里已有的同名样式（同类别）跳过
                var existing = new Dictionary<string, List<string>>();
                using (Transaction dtr = dstDb.TransactionManager.StartTransaction())
                {
                    foreach (string cat in perCat.Keys)
                    {
                        var lst = new List<string>();
                        var dcoll = ResolveStyleCollection(dstCiv, cat) as System.Collections.IEnumerable;
                        if (dcoll != null)
                            foreach (object item in dcoll)
                            {
                                if (!(item is ObjectId)) continue;
                                ObjectId oid = (ObjectId)item;
                                if (oid.IsErased) continue;
                                string nm;
                                try { nm = TryGetName(dtr.GetObject(oid, OpenMode.ForRead)); } catch { continue; }
                                if (nm != null) lst.Add(nm);
                            }
                        existing[cat] = lst;
                    }
                    dtr.Commit();
                }

                foreach (string cat in perCat.Keys)
                {
                    var ids = new ObjectIdCollection();
                    var names = new List<string>();
                    foreach (var pair in perCat[cat])
                    {
                        if (mode == "ignore" && existing[cat].Contains(pair.Value))
                        { skipped.Add(new JsonObject { ["cat"] = cat, ["name"] = pair.Value, ["why"] = "目标图已有同名" }); continue; }
                        ids.Add(pair.Key);
                        names.Add(pair.Value);
                    }
                    if (ids.Count == 0) continue;

                    foreach (string nm in names) planned.Add(cat + " :: " + nm);
                    if (dry) continue;

                    // Civil 样式跨库导入的正路是 StyleBase.ExportTo（自动带依赖子样式）；
                    // WblockCloneObjects 对 Civil 样式一律报 eInvalidOwnerObject（2026-08-11 实测）。
                    // ignore 模式下同名已在上面跳过，此处解决器统一用 Override。
                    using (Transaction str2 = srcDb.TransactionManager.StartTransaction())
                    {
                        for (int i2 = 0; i2 < ids.Count; i2++)
                        {
                            try
                            {
                                var srcObj = str2.GetObject(ids[i2], OpenMode.ForRead);
                                var styleBase = srcObj as Autodesk.Civil.DatabaseServices.Styles.StyleBase;
                                if (styleBase == null)
                                    throw new InvalidOperationException(
                                        "该类型不是 StyleBase，ExportTo 不适用: " + srcObj.GetType().Name);
                                styleBase.ExportTo(dstDb,
                                    Autodesk.Civil.StyleConflictResolverType.Override);
                                done.Add(cat + " :: " + names[i2]);
                            }
                            catch (System.Exception ex)
                            {
                                failed.Add(new JsonObject
                                {
                                    ["cat"] = cat,
                                    ["name"] = names[i2],
                                    ["why"] = ex.GetType().Name + ": " + Truncate(
                                        ex.InnerException != null ? ex.InnerException.Message : ex.Message, 120)
                                });
                            }
                        }
                        str2.Commit();
                    }
                }
            }

            return new JsonObject
            {
                ["dry_run"] = dry,
                ["from"] = from,
                ["mode"] = mode,
                ["planned_count"] = planned.Count,
                ["imported_count"] = done.Count,
                ["planned"] = planned,
                ["imported"] = done,
                ["skipped"] = skipped,
                ["failed"] = failed
            };
        }

        // 代码集是「代码 → 样式 / 标签样式」的映射表。断面图上标注不出来，
        // 十有八九是代码集里压根没给那个代码挂标签，或者挂的代码名跟走廊实际产出的对不上。
        // CodeSetStyleItem 的属性名不去猜，反射列全，ObjectId 一律解析成名字。
        static JsonNode CodeSetDump(JsonObject a, Document doc)
        {
            string want = GetString(a, "name", null);
            string corName = GetString(a, "corridor", null);
            string styleTypeFilter = GetString(a, "style_type", null);

            Database db = doc.Database;
            CivDoc civ = null;
            try { civ = CivDoc.GetCivilDocument(db); } catch { }
            if (civ == null) throw new InvalidOperationException("取不到 CivilDocument。");

            var res = new JsonObject();
            var sets = new JsonArray();
            var usedCodes = new List<string>();
            var usedLinkCodes = new List<string>();
            var usedPointCodes = new List<string>();
            var usedShapeCodes = new List<string>();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // 走廊实际产出的代码
                if (corName != null)
                {
                    foreach (ObjectId id in ModelSpace(db, tr))
                    {
                        var cor = tr.GetObject(id, OpenMode.ForRead)
                                  as Autodesk.Civil.DatabaseServices.Corridor;
                        if (cor == null || cor.Name != corName) continue;
                        try { foreach (string c in cor.GetLinkCodes()) {
                            if (!usedCodes.Contains(c)) usedCodes.Add(c);
                            if (!usedLinkCodes.Contains(c)) usedLinkCodes.Add(c);
                        } }
                        catch { }
                        try { foreach (string c in cor.GetPointCodes()) {
                            if (!usedCodes.Contains(c)) usedCodes.Add(c);
                            if (!usedPointCodes.Contains(c)) usedPointCodes.Add(c);
                        } }
                        catch { }
                        try { foreach (string c in cor.GetShapeCodes()) {
                            if (!usedCodes.Contains(c)) usedCodes.Add(c);
                            if (!usedShapeCodes.Contains(c)) usedShapeCodes.Add(c);
                        } }
                        catch { }
                    }
                }

                var coll = civ.Styles.CodeSetStyles as System.Collections.IEnumerable;
                foreach (object item in coll)
                {
                    if (!(item is ObjectId)) continue;
                    ObjectId oid = (ObjectId)item;
                    if (oid.IsErased) continue;
                    DBObject so;
                    try { so = tr.GetObject(oid, OpenMode.ForRead); } catch { continue; }
                    string nm = TryGetName(so);
                    if (nm == null) continue;
                    if (want != null && nm != want) continue;

                    var entry = new JsonObject { ["name"] = nm };
                    if (!string.IsNullOrEmpty(styleTypeFilter))
                    {
                        try { so.UpgradeOpen(); } catch { }
                        var pSubType = so.GetType().GetProperty("SubentityStyleType");
                        if (pSubType == null || !pSubType.CanWrite || !pSubType.PropertyType.IsEnum)
                            throw new InvalidOperationException("代码集 SubentityStyleType 不可写，无法按类型质检。");
                        string enumName = styleTypeFilter.Equals("link", StringComparison.OrdinalIgnoreCase)
                            ? "LinkType"
                            : styleTypeFilter.Equals("shape", StringComparison.OrdinalIgnoreCase)
                                ? "ShapeType" : "MarkerType";
                        pSubType.SetValue(so, Enum.Parse(pSubType.PropertyType, enumName), null);
                        entry["style_type_filter"] = enumName;
                    }
                    var items = new JsonArray();
                    var covered = new List<string>();
                    var coveredLinks = new List<string>();
                    var coveredPoints = new List<string>();
                    var coveredShapes = new List<string>();

                    var en = so as System.Collections.IEnumerable;
                    if (en != null)
                    {
                        foreach (object it in en)
                        {
                            if (it == null) continue;
                            var row = new JsonObject();
                            foreach (var p in it.GetType().GetProperties(
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                            {
                                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                                object v;
                                try { v = p.GetValue(it, null); } catch { continue; }
                                if (v == null) continue;
                                if (v is ObjectId)
                                {
                                    ObjectId vid = (ObjectId)v;
                                    if (vid.IsNull) { row[p.Name] = "(未设置)"; continue; }
                                    string sn = null;
                                    try { sn = TryGetName(tr.GetObject(vid, OpenMode.ForRead)); } catch { }
                                    row[p.Name] = sn ?? "(取不到名字)";
                                }
                                else if (v is string || v.GetType().IsPrimitive || v.GetType().IsEnum)
                                    row[p.Name] = v.ToString();
                            }
                            if (row["Code"] != null)
                            {
                                string code = row["Code"].ToString();
                                covered.Add(code);
                                string st = row["StyleType"] == null ? "" : row["StyleType"].ToString();
                                if (st == "LinkType") coveredLinks.Add(code);
                                else if (st == "MarkerType") coveredPoints.Add(code);
                                else if (st == "ShapeType") coveredShapes.Add(code);
                            }
                            items.Add(row);
                        }
                    }
                    entry["item_count"] = items.Count;
                    entry["items"] = items;

                    if (usedCodes.Count > 0)
                    {
                        var missing = new JsonArray();
                        foreach (string c in usedCodes)
                            if (!covered.Contains(c)) missing.Add(c);
                        entry["corridor"] = corName;
                        entry["codes_not_covered"] = missing;
                        var missingLinks = new JsonArray();
                        foreach (string c in usedLinkCodes)
                            if (!coveredLinks.Contains(c)) missingLinks.Add(c);
                        var missingPoints = new JsonArray();
                        foreach (string c in usedPointCodes)
                            if (!coveredPoints.Contains(c)) missingPoints.Add(c);
                        var missingShapes = new JsonArray();
                        foreach (string c in usedShapeCodes)
                            if (!coveredShapes.Contains(c)) missingShapes.Add(c);
                        entry["link_codes_not_covered"] = missingLinks;
                        entry["point_codes_not_covered"] = missingPoints;
                        entry["shape_codes_not_covered"] = missingShapes;
                    }
                    sets.Add(entry);
                }
                tr.Commit();
            }

            if (usedCodes.Count > 0)
            {
                var uc = new JsonArray();
                foreach (string c in usedCodes) uc.Add(c);
                res["corridor_codes"] = uc;
            }
            res["code_sets"] = sets;
            return res;
        }

        // 按名字在若干样式集合里找 ObjectId（代码集的样式可能是链接/标记/形状/特征线，标签同理）
        static ObjectId FindStyleAnywhere(Transaction tr, CivDoc civ, string name, string[] cats)
        {
            foreach (string cat in cats)
            {
                object coll;
                try { coll = ResolveStyleCollection(civ, cat); } catch { continue; }
                var en = coll as System.Collections.IEnumerable;
                if (en == null) continue;
                foreach (object item in en)
                {
                    if (!(item is ObjectId)) continue;
                    ObjectId oid = (ObjectId)item;
                    if (oid.IsErased) continue;
                    string nm;
                    try { nm = TryGetName(tr.GetObject(oid, OpenMode.ForRead)); } catch { continue; }
                    if (nm == name) return oid;   // 大小写敏感：@C3DF-centerline 与 @C3DF-CenterLine 是两个东西
                }
            }
            return ObjectId.Null;
        }

        static readonly string[] CodeStyleCats = {
            "LinkStyles", "MarkerStyles", "ShapeStyles", "FeatureLineStyles" };
        static readonly string[] CodeLabelCats = {
            "LabelStyles.GeneralLinkLabelStyles", "LabelStyles.GeneralMarkerLabelStyles",
            "LabelStyles.GeneralShapeLabelStyles" };

        static JsonNode CodeSetEdit(JsonObject a, Document doc)
        {
            string csName = GetString(a, "name", null);
            if (csName == null) throw new InvalidOperationException("code_set_edit 需要 name");
            var items = a["items"] as JsonArray;
            if (items == null || items.Count == 0)
                throw new InvalidOperationException("code_set_edit 需要 items:[{code,style?,label_style?}]");
            bool dry = GetBool(a, "dry_run", true);

            Database db = doc.Database;
            CivDoc civ = null;
            try { civ = CivDoc.GetCivilDocument(db); } catch { }
            if (civ == null) throw new InvalidOperationException("取不到 CivilDocument。");

            var done = new JsonArray();
            var failed = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                DBObject csObj = null;
                var coll = civ.Styles.CodeSetStyles as System.Collections.IEnumerable;
                foreach (object item in coll)
                {
                    if (!(item is ObjectId)) continue;
                    ObjectId oid = (ObjectId)item;
                    if (oid.IsErased) continue;
                    DBObject o;
                    try { o = tr.GetObject(oid, OpenMode.ForRead); } catch { continue; }
                    if (TryGetName(o) == csName) { csObj = o; break; }
                }
                if (csObj == null) throw new InvalidOperationException("找不到代码集: " + csName);

                if (!dry) { try { csObj.UpgradeOpen(); } catch { } }

                foreach (JsonNode n in items)
                {
                    var o = n as JsonObject; if (o == null) continue;
                    string code = o["code"] == null ? null : o["code"].ToString();
                    string styleName = o["style"] == null ? null : o["style"].ToString();
                    string labelName = o["label_style"] == null ? null : o["label_style"].ToString();
                    string styleType = o["style_type"] == null ? null : o["style_type"].ToString().ToLowerInvariant();
                    if (code == null) continue;

                    ObjectId styleId = ObjectId.Null, labelId = ObjectId.Null;
                    if (styleName != null)
                    {
                        string[] styleCats = styleType == "link" ? new string[] { "LinkStyles" }
                            : styleType == "point" || styleType == "marker" ? new string[] { "MarkerStyles" }
                            : styleType == "shape" ? new string[] { "ShapeStyles" }
                            : CodeStyleCats;
                        styleId = FindStyleAnywhere(tr, civ, styleName, styleCats);
                        if (styleId.IsNull)
                        { failed.Add(new JsonObject { ["code"] = code, ["why"] = "找不到" + (styleType ?? "指定类型") + "样式 " + styleName }); continue; }
                    }
                    if (labelName != null)
                    {
                        string[] labelCats = styleType == "link"
                            ? new string[] { "LabelStyles.GeneralLinkLabelStyles" }
                            : styleType == "point" || styleType == "marker"
                                ? new string[] { "LabelStyles.GeneralMarkerLabelStyles" }
                                : styleType == "shape"
                                    ? new string[] { "LabelStyles.GeneralShapeLabelStyles" }
                                    : CodeLabelCats;
                        labelId = FindStyleAnywhere(tr, civ, labelName, labelCats);
                        if (labelId.IsNull)
                        { failed.Add(new JsonObject { ["code"] = code, ["why"] = "找不到标签样式 " + labelName }); continue; }
                    }

                    if (dry)
                    {
                        done.Add(code + " → 样式 " + (styleName ?? "(不改)")
                                 + " / 标签 " + (labelName ?? "(不改)") + "（预演）");
                        continue;
                    }

                    try
                    {
                        // CodeSetStyle 的枚举器、GetItemBy 和 Add 都受当前
                        // SubentityStyleType 控制。必须先切到目标组，再查找已有代码。
                        if (styleType != null)
                        {
                            var pSubType = csObj.GetType().GetProperty("SubentityStyleType");
                            if (pSubType == null || !pSubType.CanWrite || !pSubType.PropertyType.IsEnum)
                                throw new InvalidOperationException("代码集 SubentityStyleType 不可写");
                            string enumName = styleType == "link" ? "LinkType"
                                : styleType == "shape" ? "ShapeType" : "MarkerType";
                            pSubType.SetValue(csObj, Enum.Parse(pSubType.PropertyType, enumName), null);
                        }

                        // 已有同名代码就取出来改，没有就 Add
                        object entry = null;
                        var mGet = csObj.GetType().GetMethod("GetItemBy");
                        var en2 = csObj as System.Collections.IEnumerable;
                        if (en2 != null)
                            foreach (object it in en2)
                            {
                                var pc = it.GetType().GetProperty("Code");
                                if (pc == null) continue;
                                object cv = null;
                                try { cv = pc.GetValue(it, null); } catch { }
                                if (cv != null && cv.ToString() == code) { entry = it; break; }
                            }

                        if (entry == null)
                        {
                            // CodeSetStyle.Add(code, styleId) 依据样式集当前的
                            // SubentityStyleType 决定把新代码放进 Link/Point/Shape
                            // 哪一组；默认值是 MarkerType，不能只靠 styleId 推断。
                            if (styleType != null)
                            {
                                var pSubType = csObj.GetType().GetProperty("SubentityStyleType");
                                if (pSubType == null || !pSubType.CanWrite || !pSubType.PropertyType.IsEnum)
                                    throw new InvalidOperationException("代码集 SubentityStyleType 不可写");
                                string enumName = styleType == "link" ? "LinkType"
                                    : styleType == "shape" ? "ShapeType" : "MarkerType";
                                pSubType.SetValue(csObj, Enum.Parse(pSubType.PropertyType, enumName), null);
                            }
                            var mAdd = csObj.GetType().GetMethod("Add",
                                new Type[] { typeof(string), typeof(ObjectId) });
                            if (mAdd == null) throw new InvalidOperationException("代码集没有 Add(string,ObjectId)");
                            entry = mAdd.Invoke(csObj, new object[] { code, styleId });
                        }
                        else if (!styleId.IsNull)
                        {
                            var ps = entry.GetType().GetProperty("CodeStyleId");
                            if (ps != null && ps.CanWrite) ps.SetValue(entry, styleId, null);
                        }

                        if (entry != null && styleType != null)
                        {
                            var pt = entry.GetType().GetProperty("StyleType");
                            string actual = pt == null ? "(未知)" : Convert.ToString(pt.GetValue(entry, null));
                            string expected = styleType == "link" ? "LinkType"
                                : styleType == "shape" ? "ShapeType" : "MarkerType";
                            if (actual != expected)
                                throw new InvalidOperationException(
                                    "代码类型错误，期望 " + expected + "，实际 " + actual);
                        }

                        if (!labelId.IsNull && entry != null)
                        {
                            var pl = entry.GetType().GetProperty("LabelStyleId");
                            if (pl == null || !pl.CanWrite)
                                throw new InvalidOperationException("LabelStyleId 不可写");
                            try
                            {
                                pl.SetValue(entry, labelId, null);
                            }
                            catch
                            {
                                // Civil 3D 2025 对部分新建 Link Code 直接写 ObjectId
                                // 会报“Value does not fall within expected range”，但同一
                                // CodeSetStyleItem 通过 LabelStyleName 可由宿主按类型解析。
                                var pn = entry.GetType().GetProperty("LabelStyleName");
                                if (pn == null || !pn.CanWrite) throw;
                                pn.SetValue(entry, labelName, null);
                            }
                        }
                        done.Add(code + " → 样式 " + (styleName ?? "(不改)")
                                 + " / 标签 " + (labelName ?? "(不改)"));
                    }
                    catch (System.Exception ex)
                    {
                        failed.Add(new JsonObject
                        {
                            ["code"] = code,
                            ["why"] = ex.GetType().Name + ": " + Truncate(
                                ex.InnerException != null ? ex.InnerException.Message : ex.Message, 120)
                        });
                    }
                }
                tr.Commit();
            }
            return new JsonObject
            {
                ["dry_run"] = dry,
                ["code_set"] = csName,
                ["done"] = done,
                ["failed"] = failed
            };
        }

        // 标签样式没有 DisplayStyle，走 LabelStyle 自己的组件模型：
        // GetComponentsDrawOrder() 拿组件 ObjectId，逐个反射读属性（可见性、文字、图层、颜色…）。
        // 标注不显示的常见原因：组件数为 0、组件 Visible=false、文字内容为空、图层被关。
        static JsonNode LabelStyleDump(JsonObject a, Document doc)
        {
            string want = GetString(a, "name", null);
            if (want == null) throw new InvalidOperationException("label_style_dump 需要 name");

            var cats = new List<string>();
            var arr = a["cats"] as JsonArray;
            if (arr != null) foreach (JsonNode n in arr) if (n != null) cats.Add(n.ToString());

            Database db = doc.Database;
            CivDoc civ = null;
            try { civ = CivDoc.GetCivilDocument(db); } catch { }
            if (civ == null) throw new InvalidOperationException("取不到 CivilDocument。");

            var hits = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // 没给类别就把整棵 LabelStyles 树扫一遍
                var collections = new List<KeyValuePair<string, object>>();
                if (cats.Count > 0)
                    foreach (string c in cats)
                    {
                        object o2 = null;
                        try { o2 = ResolveStyleCollection(civ, c); } catch { }
                        if (o2 != null) collections.Add(new KeyValuePair<string, object>(c, o2));
                    }
                else
                {
                    var stack = new List<KeyValuePair<string, object>>();
                    stack.Add(new KeyValuePair<string, object>("LabelStyles", civ.Styles.LabelStyles));
                    var seen = new List<object>();
                    while (stack.Count > 0)
                    {
                        var cur = stack[0]; stack.RemoveAt(0);
                        if (cur.Value == null) continue;
                        bool dup = false;
                        foreach (object s in seen) if (ReferenceEquals(s, cur.Value)) { dup = true; break; }
                        if (dup) continue;
                        seen.Add(cur.Value);
                        foreach (var p in cur.Value.GetType().GetProperties(
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        {
                            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                            object val;
                            try { val = p.GetValue(cur.Value, null); } catch { continue; }
                            if (val == null) continue;
                            string key = cur.Key + "." + p.Name;
                            if (val is System.Collections.IEnumerable && !(val is string))
                                collections.Add(new KeyValuePair<string, object>(key, val));
                            else if (val.GetType().FullName != null
                                     && val.GetType().FullName.StartsWith("Autodesk.Civil")
                                     && val.GetType().Name.EndsWith("Root"))
                                stack.Add(new KeyValuePair<string, object>(key, val));
                        }
                    }
                }

                foreach (var kv in collections)
                {
                    var en = kv.Value as System.Collections.IEnumerable;
                    if (en == null) continue;
                    foreach (object item in en)
                    {
                        if (!(item is ObjectId)) continue;
                        ObjectId oid = (ObjectId)item;
                        if (oid.IsErased) continue;
                        DBObject so;
                        try { so = tr.GetObject(oid, OpenMode.ForRead); } catch { continue; }
                        if (TryGetName(so) != want) continue;   // 大小写敏感

                        var entry = new JsonObject { ["cat"] = kv.Key, ["name"] = want,
                                                     ["type"] = so.GetType().Name };

                        // 组件数
                        try
                        {
                            var mCnt = so.GetType().GetMethod("GetComponentsCount", Type.EmptyTypes);
                            if (mCnt != null) entry["component_count"] = (int)mCnt.Invoke(so, null);
                        }
                        catch (System.Exception ex)
                        { entry["component_count_error"] = ex.GetType().Name; }

                        // 逐个组件
                        var comps = new JsonArray();
                        try
                        {
                            var mOrder = so.GetType().GetMethod("GetComponentsDrawOrder", Type.EmptyTypes);
                            var ids = mOrder == null ? null : mOrder.Invoke(so, null) as ObjectId[];
                            if (ids != null)
                                foreach (ObjectId cid in ids)
                                {
                                    if (cid.IsNull || cid.IsErased) continue;
                                    object c;
                                    try { c = tr.GetObject(cid, OpenMode.ForRead); } catch { continue; }
                                    comps.Add(DumpShallow(tr, c, 2));
                                }
                        }
                        catch (System.Exception ex)
                        { entry["components_error"] = ex.GetType().Name + ": " + Truncate(ex.Message, 90); }
                        entry["components"] = comps;

                        // 样式自身的 Properties（可见性、图层等都在这）
                        try
                        {
                            var pProps = so.GetType().GetProperty("Properties");
                            if (pProps != null)
                            {
                                object pv = pProps.GetValue(so, null);
                                if (pv != null) entry["properties"] = DumpShallow(tr, pv, 2);
                            }
                        }
                        catch (System.Exception ex)
                        { entry["properties_error"] = ex.GetType().Name; }

                        hits.Add(entry);
                    }
                }
                tr.Commit();
            }
            return new JsonObject { ["name"] = want, ["found"] = hits.Count, ["hits"] = hits };
        }

        // 浅层反射转 JSON：标量直出，ObjectId 解析成名字，嵌套对象再往下 depth 层
        static JsonObject DumpShallow(Transaction tr, object o, int depth)
        {
            var row = new JsonObject();
            if (o == null) return row;
            foreach (var p in o.GetType().GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                object v;
                try { v = p.GetValue(o, null); }
                catch (System.Exception ex)
                {
                    string msg = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                    if (msg.IndexOf("not overridable", StringComparison.OrdinalIgnoreCase) < 0)
                        row[p.Name] = "(取值失败: " + ex.GetType().Name + ")";
                    continue;
                }
                if (v == null) continue;
                Type vt = v.GetType();
                if (v is ObjectId)
                {
                    ObjectId vid = (ObjectId)v;
                    if (vid.IsNull) { row[p.Name] = "(空)"; continue; }
                    string sn = null;
                    try { sn = TryGetName(tr.GetObject(vid, OpenMode.ForRead)); } catch { }
                    row[p.Name] = sn ?? "(取不到名字)";
                }
                else if (v is string || vt.IsPrimitive || vt.IsEnum)
                    row[p.Name] = v.ToString();
                else if (depth > 0 && vt.FullName != null && vt.FullName.StartsWith("Autodesk"))
                    row[p.Name] = DumpShallow(tr, v, depth - 1);
            }
            return row;
        }

        // 按路线各取一个断面 + 断面图，逐属性 dump。
        // 用途：同一张图里「Civil 命令生成的旧断面」和「本工具生成的新断面」并存时直接对差。
        static JsonNode DumpSections(JsonObject a, Document doc)
        {
            var names = new List<string>();
            var arr = a["alignments"] as JsonArray;
            if (arr != null) foreach (JsonNode n in arr) if (n != null) names.Add(n.ToString());
            if (names.Count == 0) throw new InvalidOperationException("dump_sections 需要 alignments");
            string which = GetString(a, "which", "both");

            Database db = doc.Database;
            var res = new JsonObject();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (string alName in names)
                {
                    var entry = new JsonObject();
                    CivAlignment al = null;
                    foreach (ObjectId id in ModelSpace(db, tr))
                    {
                        var x = tr.GetObject(id, OpenMode.ForRead) as CivAlignment;
                        if (x != null && x.Name == alName) { al = x; break; }
                    }
                    if (al == null) { entry["error"] = "找不到路线"; res[alName] = entry; continue; }

                    // 采样线组 → 采样线 → 断面。**所有组、所有断面图都要**：
                    // 同一条路线上可能并存「我生成的」和「Civil 命令生成的」两组，
                    // 只取第一个就永远比不出差异（前几轮就栽在这）。
                    var groups = new JsonArray();
                    try
                    {
                        foreach (ObjectId gid in al.GetSampleLineGroupIds())
                        {
                            bool gotSection = false, gotView = false;
                            var grp = tr.GetObject(gid, OpenMode.ForRead)
                                      as Autodesk.Civil.DatabaseServices.SampleLineGroup;
                            if (grp == null) continue;
                            var gEntry = new JsonObject { ["group"] = grp.Name };
                            entry = gEntry;                     // 下面沿用 entry 变量填充本组
                            groups.Add(gEntry);
                            entry["sample_line_group"] = grp.Name;

                            foreach (ObjectId sid in grp.GetSampleLineIds())
                            {
                                var sl = tr.GetObject(sid, OpenMode.ForRead)
                                         as Autodesk.Civil.DatabaseServices.SampleLine;
                                if (sl == null) continue;
                                entry["sample_line"] = sl.Name;
                                entry["sample_line_dump"] = DumpShallow(tr, sl, 1);

                                // 断面图直接从采样线拿。**同一条采样线下可能挂多张**——
                                // 一个采样线组下能有多个断面图组（我生成的一个、Civil 命令生成的一个），
                                // 只取第一张就永远看不到另一组。
                                if (which != "section" && !gotView)
                                    try
                                    {
                                        var views = new JsonArray();
                                        foreach (ObjectId svId in sl.GetSectionViewIds())
                                        {
                                            var sv2 = tr.GetObject(svId, OpenMode.ForRead);
                                            var one = DumpShallow(tr, sv2, 1);
                                            one["_name"] = TryGetName(sv2);

                                            // ★ 视图级的标注组查询——判定「标注到底有没有被创建」。
                                            // 集合非空但屏幕看不见 → 显示优化/图层/可见性问题；
                                            // 集合为空 → 标签集根本没应用上。
                                            var lg = new JsonObject();
                                            foreach (var mm in sv2.GetType().GetMethods(
                                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                                            {
                                                if (mm.GetParameters().Length != 0) continue;
                                                if (mm.Name.IndexOf("Label", StringComparison.Ordinal) < 0) continue;
                                                if (!mm.Name.StartsWith("Get")) continue;
                                                try
                                                {
                                                    object rv = mm.Invoke(sv2, null);
                                                    var oc = rv as ObjectIdCollection;
                                                    lg[mm.Name] = oc == null ? -1 : oc.Count;
                                                }
                                                catch (System.Exception ex)
                                                { lg[mm.Name] = "err:" + ex.GetType().Name; }
                                            }
                                            one["_label_groups"] = lg;

                                            // ★ 每个视图对每个断面的**覆盖项**——两个视图组共用同一批断面，
                                            // 所以显示差异只可能藏在这里（道路横断面的覆盖也在其中）。
                                            try
                                            {
                                                var pOv = sv2.GetType().GetProperty("GraphOverrides");
                                                object ov = pOv == null ? null : pOv.GetValue(sv2, null);
                                                var ovs = new JsonArray();
                                                var en3 = ov as System.Collections.IEnumerable;
                                                if (en3 != null)
                                                    foreach (object it in en3)
                                                    {
                                                        var row = DumpShallow(tr, it, 1);
                                                        // ★ 这个断面在这个视图里到底有没有标注组——
                                                        // Autodesk 支持文章说标注会默认成「走廊点样式标注」而非代码集标注，
                                                        // 有没有 label group 是最直接的判据
                                                        try
                                                        {
                                                            var m2 = it.GetType().GetMethod("GetSectionLabelGroupIds", Type.EmptyTypes);
                                                            var ids2 = m2 == null ? null : m2.Invoke(it, null) as ObjectIdCollection;
                                                            row["_label_group_count"] = ids2 == null ? -1 : ids2.Count;
                                                        }
                                                        catch (System.Exception ex)
                                                        { row["_label_group_error"] = ex.GetType().Name; }
                                                        ovs.Add(row);
                                                    }
                                                one["_overrides"] = ovs;
                                                one["_override_count"] = ovs.Count;
                                            }
                                            catch (System.Exception ex)
                                            { one["_override_error"] = ex.GetType().Name + ": " + Truncate(ex.Message, 90); }

                                            views.Add(one);
                                        }
                                        entry["views"] = views;
                                        entry["view_count"] = views.Count;
                                        gotView = true;
                                    }
                                    catch (System.Exception ex)
                                    { entry["view_error"] = ex.GetType().Name + ": " + Truncate(ex.Message, 90); }

                                // 一条采样线下有多个断面（地面线一个、走廊一个…），全都要，
                                // 只取第一个会拿到地面线断面，而点标注归走廊断面管。
                                if (which != "view" && !gotSection)
                                    try
                                    {
                                        var secs = new JsonArray();
                                        foreach (ObjectId secId in sl.GetSectionIds())
                                        {
                                            var sec = tr.GetObject(secId, OpenMode.ForRead);
                                            var one = DumpShallow(tr, sec, 1);
                                            one["_type"] = sec.GetType().Name;
                                            secs.Add(one);
                                        }
                                        entry["sections"] = secs;
                                        entry["section_count"] = secs.Count;
                                        gotSection = true;
                                    }
                                    catch (System.Exception ex)
                                    { entry["section_error"] = ex.GetType().Name + ": " + Truncate(ex.Message, 90); }
                                break;   // 每组只取第一条采样线做样本，够对比了
                            }
                        }
                    }
                    catch (System.Exception ex)
                    { groups.Add(new JsonObject { ["group_error"] = ex.GetType().Name + ": " + Truncate(ex.Message, 90) }); }

                    res[alName] = new JsonObject { ["group_count"] = groups.Count, ["groups"] = groups };
                }
                tr.Commit();
            }
            return res;
        }

        // 不知道该用哪个 API 时先搜。
        // ⚠ AeccDbMgd 在自定义 ALC 里，AppDomain.CurrentDomain.GetAssemblies() 看不到它，
        // 必须拿 typeof(Alignment).Assembly 当种子（老坑，见 reference-accoreconsole-civil3d）。
        static JsonNode ApiSearch(JsonObject a, Document doc)
        {
            string q = GetString(a, "q", null);
            if (q == null) throw new InvalidOperationException("api_search 需要 q");
            bool withMembers = GetBool(a, "members", true);
            int max = (int)GetDouble(a, "max", 60);
            string[] terms = q.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            var asms = new List<System.Reflection.Assembly>();
            asms.Add(typeof(CivAlignment).Assembly);                 // AeccDbMgd
            try { asms.Add(typeof(CivDoc).Assembly); } catch { }     // AeccUiMgd / ApplicationServices
            try { asms.Add(typeof(Database).Assembly); } catch { }   // acdbmgd

            Func<string, bool> hit = s =>
            {
                foreach (string t in terms)
                    if (s.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0) return false;
                return true;
            };

            var types = new JsonArray();
            var members = new JsonArray();
            var seenAsm = new List<string>();

            foreach (var asm in asms)
            {
                string an = asm.GetName().Name;
                if (seenAsm.Contains(an)) continue;
                seenAsm.Add(an);
                Type[] all;
                try { all = asm.GetTypes(); } catch { continue; }
                foreach (Type t in all)
                {
                    if (t.FullName == null) continue;
                    if (hit(t.Name) && types.Count < max)
                        types.Add(an + " :: " + t.FullName);

                    if (!withMembers || members.Count >= max) continue;
                    if (!t.IsPublic) continue;
                    System.Reflection.MethodInfo[] ms;
                    try
                    {
                        ms = t.GetMethods(System.Reflection.BindingFlags.Public
                                        | System.Reflection.BindingFlags.Instance
                                        | System.Reflection.BindingFlags.Static
                                        | System.Reflection.BindingFlags.DeclaredOnly);
                    }
                    catch { continue; }
                    foreach (var m in ms)
                    {
                        if (members.Count >= max) break;
                        // 既按成员名匹配，也按**签名里出现的类型名**匹配。
                        // 后者是关键：想知道「谁用了 eXxx 这个枚举」时，成员名里根本不含它。
                        var ps = new List<string>();
                        bool sigHit = hit(m.ReturnType.Name);
                        foreach (var p in m.GetParameters())
                        {
                            ps.Add(p.ParameterType.Name + " " + p.Name);
                            if (hit(p.ParameterType.Name)) sigHit = true;
                        }
                        if (!hit(m.Name) && !sigHit) continue;
                        members.Add(t.Name + "." + m.Name + "(" + string.Join(", ", ps.ToArray()) + ")"
                                    + " → " + m.ReturnType.Name);
                    }
                }
            }

            return new JsonObject
            {
                ["query"] = q,
                ["assemblies"] = string.Join(", ", seenAsm.ToArray()),
                ["type_hits"] = types.Count,
                ["types"] = types,
                ["member_hits"] = members.Count,
                ["members"] = members
            };
        }

        static JsonNode ListLayers(JsonObject a, Document doc)
        {
            bool usedOnly = GetBool(a, "used_only", false);
            int max = (int)GetDouble(a, "max", 2000);
            Database db = doc.Database;
            var arr = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // 先统计模型空间里各图层的实体数，判断「有没有被用」
                var used = new Dictionary<string, int>();
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    Entity e;
                    try { e = tr.GetObject(id, OpenMode.ForRead) as Entity; } catch { continue; }
                    if (e == null) continue;
                    string ln;
                    try { ln = e.Layer; } catch { continue; }
                    used[ln] = used.ContainsKey(ln) ? used[ln] + 1 : 1;
                }

                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in lt)
                {
                    if (arr.Count >= max) break;
                    var ltr = tr.GetObject(id, OpenMode.ForRead) as LayerTableRecord;
                    if (ltr == null) continue;
                    int n = used.ContainsKey(ltr.Name) ? used[ltr.Name] : 0;
                    if (usedOnly && n == 0) continue;
                    arr.Add(new JsonObject
                    {
                        ["name"] = ltr.Name,
                        ["color"] = DescribeColor(ltr.Color),
                        ["linetype"] = SafeLinetype(tr, ltr),
                        ["lineweight"] = ltr.LineWeight.ToString(),
                        ["off"] = ltr.IsOff,
                        ["frozen"] = ltr.IsFrozen,
                        ["locked"] = ltr.IsLocked,
                        ["plottable"] = ltr.IsPlottable,
                        ["entities"] = n
                    });
                }
                tr.Commit();
            }
            return new JsonObject { ["count"] = arr.Count, ["layers"] = arr };
        }

        static string SafeLinetype(Transaction tr, LayerTableRecord ltr)
        {
            try
            {
                var lt = tr.GetObject(ltr.LinetypeObjectId, OpenMode.ForRead) as LinetypeTableRecord;
                return lt == null ? "?" : lt.Name;
            }
            catch { return "?"; }
        }

        // 批量版 style_display：所有（或过滤后的）样式 × 所有视图 × 所有分量。
        // 归一化前先靠它看清全貌——哪些颜色不是 ByLayer、图层名有几种写法、有没有指向不存在的图层。
        static JsonNode StylesAudit(JsonObject a, Document doc)
        {
            string filter = GetString(a, "filter", null);
            int max = (int)GetDouble(a, "max", 2000);
            var onlyCats = a["cats"] as JsonArray;

            Database db = doc.Database;
            CivDoc civ = null;
            try { civ = CivDoc.GetCivilDocument(db); } catch { }
            if (civ == null) throw new InvalidOperationException("取不到 CivilDocument。");

            // 图层表：判断样式引用的图层存不存在
            var layerSet = new List<string>();
            using (Transaction tr0 = db.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tr0.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in lt)
                {
                    var ltr = tr0.GetObject(id, OpenMode.ForRead) as LayerTableRecord;
                    if (ltr != null) layerSet.Add(ltr.Name);
                }
                tr0.Commit();
            }

            var rows = new JsonArray();
            int styles = 0, comps = 0, nonByLayer = 0, missingLayer = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // 复用 list_styles 的遍历：类别路径 → 集合
                var stack = new List<KeyValuePair<string, object>>();
                stack.Add(new KeyValuePair<string, object>("", civ.Styles));
                var seen = new List<object>();
                var collections = new List<KeyValuePair<string, object>>();

                while (stack.Count > 0)
                {
                    var cur = stack[0]; stack.RemoveAt(0);
                    if (cur.Value == null) continue;
                    bool dup = false;
                    foreach (object s in seen) if (ReferenceEquals(s, cur.Value)) { dup = true; break; }
                    if (dup) continue;
                    seen.Add(cur.Value);

                    foreach (var p in cur.Value.GetType().GetProperties(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    {
                        if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                        string key = cur.Key.Length == 0 ? p.Name : cur.Key + "." + p.Name;
                        object val;
                        try { val = p.GetValue(cur.Value, null); } catch { continue; }
                        if (val == null) continue;
                        Type vt = val.GetType();
                        if (val is string || vt.IsPrimitive || vt.IsEnum) continue;
                        if (val is System.Collections.IEnumerable)
                            collections.Add(new KeyValuePair<string, object>(key, val));
                        else if (vt.FullName != null && vt.FullName.StartsWith("Autodesk.Civil")
                                 && vt.Name.EndsWith("Root"))
                            stack.Add(new KeyValuePair<string, object>(key, val));
                    }
                }

                foreach (var kv in collections)
                {
                    if (onlyCats != null)
                    {
                        bool want = false;
                        foreach (JsonNode c in onlyCats)
                            if (c != null && c.ToString() == kv.Key) { want = true; break; }
                        if (!want) continue;
                    }
                    var en = kv.Value as System.Collections.IEnumerable;
                    if (en == null) continue;

                    foreach (object item in en)
                    {
                        if (rows.Count >= max) break;
                        if (!(item is ObjectId)) continue;
                        ObjectId oid = (ObjectId)item;
                        if (oid.IsErased) continue;
                        DBObject so;
                        try { so = tr.GetObject(oid, OpenMode.ForRead); } catch { continue; }
                        string nm = TryGetName(so);
                        if (nm == null) continue;
                        if (filter != null && nm.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        styles++;

                        foreach (var m in so.GetType().GetMethods(
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        {
                            if (!m.Name.StartsWith("GetDisplayStyle")) continue;
                            var ps = m.GetParameters();
                            if (ps.Length != 1 || !ps[0].ParameterType.IsEnum) continue;
                            Type et = ps[0].ParameterType;
                            string viewName = m.Name.Substring("GetDisplayStyle".Length);
                            if (viewName.Length == 0) viewName = "(default)";

                            foreach (string cn in Enum.GetNames(et))
                            {
                                if (rows.Count >= max) break;
                                var row = new JsonObject
                                {
                                    ["cat"] = kv.Key,
                                    ["style"] = nm,
                                    ["view"] = viewName,
                                    ["component"] = cn
                                };
                                try
                                {
                                    object ds = m.Invoke(so, new object[] { Enum.Parse(et, cn) });
                                    FillDisplay(ds, row);
                                    comps++;
                                    string col = row["color"] != null ? row["color"].ToString() : null;
                                    if (col != null && col != "ByLayer") { nonByLayer++; row["color_todo"] = true; }
                                    string lay = row["layer"] != null ? row["layer"].ToString() : null;
                                    if (lay != null && !layerSet.Contains(lay))
                                    { missingLayer++; row["layer_missing"] = true; }
                                }
                                catch { continue; }   // 该分量在此样式上不适用，跳过
                                rows.Add(row);
                            }
                        }
                    }
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["filter"] = filter,
                ["styles_scanned"] = styles,
                ["components"] = comps,
                ["color_not_bylayer"] = nonByLayer,
                ["layer_missing_refs"] = missingLayer,
                ["rows"] = rows
            };
        }

        // ---- style_display：样式对话框 Display 页的读写 ----
        //
        // Civil 的路子（2026-07-28 用 api 操作查实，不是猜的）：
        //   样式类上有 GetDisplayStyle<视图>(某枚举 分量) → 返回 DisplayStyle，
        //   DisplayStyle 的 Color / Layer / Linetype / Lineweight / LinetypeScale / Visible 都可写。
        // 各样式类的枚举类型各不相同（ProfileDataDisplayStyleType、AlignmentDisplayStyleType…），
        // 所以这里不写死：反射找 GetDisplayStyle* 方法，按它那个枚举参数解析分量名。
        static JsonNode StyleDisplay(JsonObject a, Document doc)
        {
            string cat = GetString(a, "cat", null);
            string name = GetString(a, "name", null);
            if (cat == null || name == null)
                throw new InvalidOperationException("style_display 需要 cat 和 name");
            string view = GetString(a, "view", "Plan");
            string comp = GetString(a, "component", null);
            var set = a["set"] as JsonObject;
            bool dry = GetBool(a, "dry_run", true);

            Database db = doc.Database;
            CivDoc civ = null;
            try { civ = CivDoc.GetCivilDocument(db); } catch { }
            if (civ == null) throw new InvalidOperationException("取不到 CivilDocument。");

            object coll = ResolveStyleCollection(civ, cat);
            var en = coll as System.Collections.IEnumerable;
            if (en == null) throw new InvalidOperationException("类别路径解析不到集合: " + cat);

            var res = new JsonObject { ["cat"] = cat, ["name"] = name, ["view"] = view, ["dry_run"] = dry };

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                DBObject styleObj = null;
                foreach (object item in en)
                {
                    if (!(item is ObjectId)) continue;
                    ObjectId oid = (ObjectId)item;
                    if (oid.IsErased) continue;
                    DBObject o;
                    try { o = tr.GetObject(oid, OpenMode.ForRead); } catch { continue; }
                    if (TryGetName(o) == name) { styleObj = o; break; }
                }
                if (styleObj == null)
                    throw new InvalidOperationException("集合 " + cat + " 里没有样式: " + name);
                res["style_type"] = styleObj.GetType().Name;

                // 找 GetDisplayStyle<视图>(枚举)
                System.Reflection.MethodInfo getter = null;
                var candidates = new List<System.Reflection.MethodInfo>();
                foreach (var m in styleObj.GetType().GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (!m.Name.StartsWith("GetDisplayStyle")) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 1 || !ps[0].ParameterType.IsEnum) continue;
                    candidates.Add(m);
                    if (m.Name.EndsWith(view, StringComparison.OrdinalIgnoreCase)) getter = m;
                }
                if (getter == null && candidates.Count == 1) getter = candidates[0];
                if (getter == null)
                {
                    var avail = new JsonArray();
                    foreach (var m in candidates) avail.Add(m.Name);
                    res["error"] = "找不到匹配 view=" + view + " 的 GetDisplayStyle 方法";
                    res["available_views"] = avail;
                    tr.Commit();
                    return res;
                }
                res["getter"] = getter.Name;
                Type enumT = getter.GetParameters()[0].ParameterType;
                res["component_enum"] = enumT.Name;

                Func<string, string> norm = s =>
                    s.Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();

                // 不给 component：列出全部分量及当前值
                if (comp == null)
                {
                    var arr = new JsonArray();
                    foreach (string en2 in Enum.GetNames(enumT))
                    {
                        var row = new JsonObject { ["component"] = en2 };
                        try
                        {
                            object ds = getter.Invoke(styleObj, new object[] { Enum.Parse(enumT, en2) });
                            FillDisplay(ds, row);
                        }
                        catch (System.Exception ex)
                        { row["error"] = ex.GetType().Name + ": " + Truncate(
                            ex.InnerException != null ? ex.InnerException.Message : ex.Message, 80); }
                        arr.Add(row);
                    }
                    res["components"] = arr;
                    tr.Commit();
                    return res;
                }

                // 指定 component：解析枚举。
                // 对话框措辞和枚举名常差一截（对话框「Band Title Box Text」= 枚举 TitleBoxText），
                // 所以先精确匹配，不中再退到「一头包含另一头」，且只在唯一命中时才认。
                string matched = null;
                string want = norm(comp);
                foreach (string en2 in Enum.GetNames(enumT))
                    if (norm(en2) == want) { matched = en2; break; }
                if (matched == null)
                {
                    var loose = new List<string>();
                    foreach (string en2 in Enum.GetNames(enumT))
                    {
                        string e = norm(en2);
                        if (want.EndsWith(e) || e.EndsWith(want) || want.Contains(e) || e.Contains(want))
                            loose.Add(en2);
                    }
                    // 多个命中时取**最长**的那个：「Band Title Box Text」同时套上 TitleBox 和
                    // TitleBoxText，更长的才是用户指的那一个。只有并列最长才算真歧义。
                    if (loose.Count > 0)
                    {
                        loose.Sort(delegate (string x, string y) { return y.Length.CompareTo(x.Length); });
                        if (loose.Count == 1 || loose[0].Length > loose[1].Length)
                        { matched = loose[0]; res["matched_loosely"] = true; }
                        else
                            throw new InvalidOperationException(
                                "分量名有歧义: " + comp + "，候选: " + string.Join(", ", loose.ToArray()));
                    }
                }
                if (matched == null)
                    throw new InvalidOperationException(
                        "分量名对不上: " + comp + "，合法值: "
                        + string.Join(", ", Enum.GetNames(enumT)));
                res["component"] = matched;

                object disp = getter.Invoke(styleObj, new object[] { Enum.Parse(enumT, matched) });
                var before = new JsonObject(); FillDisplay(disp, before);
                res["before"] = before;

                if (set == null || set.Count == 0)
                { tr.Commit(); return res; }

                var changes = new JsonArray();
                if (!dry)
                {
                    // DisplayStyle 是包装对象，改它要先把样式本身开成写
                    try { styleObj.UpgradeOpen(); } catch { }
                    disp = getter.Invoke(styleObj, new object[] { Enum.Parse(enumT, matched) });
                }
                foreach (var kv in set)
                {
                    string k = kv.Key.ToLowerInvariant();
                    string v = kv.Value == null ? null : kv.Value.ToString();
                    try
                    {
                        if (dry) { changes.Add(k + " → " + v + "（预演，未写入）"); continue; }
                        ApplyDisplay(disp, k, v);
                        changes.Add(k + " → " + v);
                    }
                    catch (System.Exception ex)
                    {
                        changes.Add(k + " 失败: " + ex.GetType().Name + ": " + Truncate(
                            ex.InnerException != null ? ex.InnerException.Message : ex.Message, 100));
                    }
                }
                res["changes"] = changes;

                var after = new JsonObject();
                FillDisplay(getter.Invoke(styleObj, new object[] { Enum.Parse(enumT, matched) }), after);
                res["after"] = after;

                tr.Commit();
            }
            return res;
        }

        static void FillDisplay(object ds, JsonObject row)
        {
            if (ds == null) { row["error"] = "DisplayStyle 为 null"; return; }
            Func<string, object> get = pn =>
            {
                var p = ds.GetType().GetProperty(pn);
                if (p == null) return null;
                try { return p.GetValue(ds, null); } catch { return null; }
            };
            object c = get("Color");
            if (c != null) row["color"] = DescribeColor(c);
            object v;
            if ((v = get("Layer")) != null) row["layer"] = v.ToString();
            if ((v = get("Linetype")) != null) row["linetype"] = v.ToString();
            if ((v = get("Lineweight")) != null) row["lineweight"] = v.ToString();
            if ((v = get("LinetypeScale")) != null) row["linetype_scale"] = Convert.ToDouble(v);
            if ((v = get("Visible")) != null) row["visible"] = Convert.ToBoolean(v);
            if ((v = get("PlotStyle")) != null) row["plot_style"] = v.ToString();
        }

        static string DescribeColor(object c)
        {
            var col = c as Autodesk.AutoCAD.Colors.Color;
            if (col == null) return c.ToString();
            if (col.IsByLayer) return "ByLayer";
            if (col.IsByBlock) return "ByBlock";
            if (col.ColorMethod == Autodesk.AutoCAD.Colors.ColorMethod.ByAci)
                return "ACI " + col.ColorIndex;
            return string.Format("{0},{1},{2}", col.Red, col.Green, col.Blue);
        }

        // 值写法：color = ByLayer|ByBlock|<ACI 0-256>|"r,g,b"；visible = true/false；其余按字符串/数字
        static void ApplyDisplay(object ds, string key, string val)
        {
            Type t = ds.GetType();
            if (key == "color")
            {
                var p = t.GetProperty("Color");
                p.SetValue(ds, ParseColor(val), null);
                return;
            }
            if (key == "visible")
            { t.GetProperty("Visible").SetValue(ds, bool.Parse(val), null); return; }
            if (key == "linetype_scale")
            { t.GetProperty("LinetypeScale").SetValue(ds, double.Parse(val, CultureInfo.InvariantCulture), null); return; }
            if (key == "lineweight")
            {
                var p = t.GetProperty("Lineweight");
                p.SetValue(ds, Enum.Parse(p.PropertyType, val, true), null);
                return;
            }
            if (key == "layer") { t.GetProperty("Layer").SetValue(ds, val, null); return; }
            if (key == "linetype") { t.GetProperty("Linetype").SetValue(ds, val, null); return; }
            if (key == "plot_style") { t.GetProperty("PlotStyle").SetValue(ds, val, null); return; }
            throw new InvalidOperationException("不认识的显示属性: " + key);
        }

        static Autodesk.AutoCAD.Colors.Color ParseColor(string v)
        {
            string s = (v ?? "").Trim();
            if (string.Equals(s, "ByLayer", StringComparison.OrdinalIgnoreCase))
                return Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                    Autodesk.AutoCAD.Colors.ColorMethod.ByLayer, 256);
            if (string.Equals(s, "ByBlock", StringComparison.OrdinalIgnoreCase))
                return Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                    Autodesk.AutoCAD.Colors.ColorMethod.ByBlock, 0);
            if (s.Contains(","))
            {
                string[] p = s.Split(',');
                return Autodesk.AutoCAD.Colors.Color.FromRgb(
                    byte.Parse(p[0].Trim()), byte.Parse(p[1].Trim()), byte.Parse(p[2].Trim()));
            }
            short aci;
            if (short.TryParse(s, out aci))
                return Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                    Autodesk.AutoCAD.Colors.ColorMethod.ByAci, aci);
            throw new InvalidOperationException("颜色写法不认识: " + v);
        }

        // 集合里可能是 ObjectId，也可能直接是对象；两种都收名字。
        static void CollectNames(Transaction tr, object collection, JsonArray outArr)
        {
            var en = collection as System.Collections.IEnumerable;
            if (en == null) return;
            foreach (object item in en)
            {
                if (item == null) continue;
                if (item is ObjectId)
                {
                    try
                    {
                        string n = TryGetName(tr.GetObject((ObjectId)item, OpenMode.ForRead));
                        if (n != null) outArr.Add(n);
                    }
                    catch { }
                }
                else if (item is DBObject)
                {
                    string n = TryGetName((DBObject)item);
                    if (n != null) outArr.Add(n);
                }
                else
                {
                    var p = item.GetType().GetProperty("Name");
                    if (p != null)
                    {
                        try { outArr.Add(System.Convert.ToString(p.GetValue(item, null))); }
                        catch { }
                    }
                }
            }
        }

        // 样式集合一律按 ObjectId 枚举 + 反射取 Name（各集合类型不同，反射最省事也最不容易猜错）
        static JsonArray StyleNames(Transaction tr, object collection, int max)
        {
            var arr = new JsonArray();
            var en = collection as System.Collections.IEnumerable;
            if (en == null) return arr;
            foreach (object item in en)
            {
                if (arr.Count >= max) { arr.Add("…(还有更多)"); break; }
                if (!(item is ObjectId)) { arr.Add(item == null ? "(null)" : item.ToString()); continue; }
                try
                {
                    DBObject o = tr.GetObject((ObjectId)item, OpenMode.ForRead);
                    string n = TryGetName(o);
                    // 取不到名字时把原因带出来，别只丢个类名让人猜
                    if (n == null)
                    {
                        string why;
                        try
                        {
                            var p = o.GetType().GetProperty("Name");
                            if (p == null) why = "(无 Name 属性)";
                            else
                            {
                                object v = p.GetValue(o, null);
                                why = v == null ? "(Name 为 null)" : v.ToString();
                            }
                        }
                        catch (System.Exception ex2) { why = "(" + ex2.GetType().Name + ": " + Truncate(ex2.Message, 60) + ")"; }
                        n = o.GetType().Name + " " + why;
                    }
                    arr.Add(n);
                }
                catch (System.Exception ex) { arr.Add("(读取失败: " + ex.GetType().Name + ")"); }
            }
            return arr;
        }

        // 新建独立 Database 写成 dwg 文件。用 new Database(true,false) 起一张空图，
        // 与 /i 传入的宿主图纸完全隔离——宿主图纸不会被改动，也不需要模板文件。
        static JsonNode CreateDwg(JsonObject a, Document doc)
        {
            string path = GetString(a, "path", null);
            if (string.IsNullOrEmpty(path))
                throw new InvalidOperationException("缺少参数 path（新 dwg 的绝对路径）。");
            if (!Path.IsPathRooted(path))
                throw new InvalidOperationException("path 必须是绝对路径：" + path);

            bool overwrite = GetBool(a, "overwrite", false);
            if (File.Exists(path) && !overwrite)
                throw new InvalidOperationException(
                    "文件已存在，拒绝覆盖：" + path + "（确需覆盖请传 overwrite:true）");

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string layer = GetString(a, "layer", "0");
            var drawn = new JsonArray();

            // 第二参 noDocument 必须为 true：旁挂数据库不关联文档。
            // 传 false 时图能正常生成，但 accoreconsole 跑完不退出（挂在 QUIT，要 taskkill）。
            using (Database db = new Database(true, true))
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                        bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    EnsureLayers(db, tr, a["layers"] as JsonArray);
                    AddEntities(db, tr, ms, a, layer, drawn);
                    InsertBlocks(db, tr, ms, a, layer, drawn);
                    tr.Commit();
                }

                if (drawn.Count == 0)
                    throw new InvalidOperationException(
                        "没有要绘制的图元——不生成空文件。可用键：blocks / circles / lines / arcs / polylines / texts。");

                db.SaveAs(path, DwgVersion.Current);
            }

            var fi = new FileInfo(path);
            return new JsonObject
            {
                ["created"] = path,
                ["bytes"] = fi.Exists ? fi.Length : 0,
                ["entities"] = drawn.Count,
                ["drawn"] = drawn
            };
        }

        // ---------- 画图：图层 + 各类图元 ----------

        // 按需建图层（已存在则只在给了 color 时更新颜色）
        static void EnsureLayers(Database db, Transaction tr, JsonArray layers)
        {
            if (layers == null) return;
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (JsonNode ln in layers)
            {
                var l = ln as JsonObject;
                if (l == null) continue;
                string name = GetString(l, "name", null);
                if (string.IsNullOrEmpty(name)) continue;
                int color = (int)GetDouble(l, "color", -1);   // ACI 1..255

                if (lt.Has(name))
                {
                    if (color >= 0 && color <= 255)
                    {
                        var rec = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForWrite);
                        rec.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                            Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)color);
                    }
                    continue;
                }
                lt.UpgradeOpen();
                var ltr = new LayerTableRecord { Name = name };
                if (color >= 0 && color <= 255)
                    ltr.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                        Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)color);
                lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
            }
        }

        // 把 a 里的 circles/lines/arcs/polylines/texts 全部画进 ms
        static void AddEntities(Database db, Transaction tr, BlockTableRecord ms,
                                JsonObject a, string defaultLayer, JsonArray drawn)
        {
            Action<Entity, JsonObject> place = (ent, spec) =>
            {
                // 顺序要紧：必须先入库再设 Layer。未入库的实体解析不了图层名，
                // 先设会抛 eKeyNotFound（堆栈指向 Entity.set_Layer）。
                ms.AppendEntity(ent);
                tr.AddNewlyCreatedDBObject(ent, true);
                string lay = GetString(spec, "layer", defaultLayer);
                if (!string.IsNullOrEmpty(lay) && lay != "0") ent.Layer = lay;
                // color：ACI 色号，不给就 ByLayer（跟既有标注同色时必须能指定）
                double ci = GetDouble(spec, "color", double.NaN);
                if (!double.IsNaN(ci))
                    ent.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                        Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)ci);
            };

            foreach (JsonObject c in Items(a, "circles"))
            {
                double r = GetDouble(c, "r", 0);
                if (r <= 0) throw new InvalidOperationException("圆半径必须大于 0（收到 r=" + r + "）。");
                double x = GetDouble(c, "x", 0), y = GetDouble(c, "y", 0);
                place(new Circle(new Point3d(x, y, 0), Vector3d.ZAxis, r), c);
                drawn.Add(new JsonObject { ["type"] = "Circle", ["x"] = x, ["y"] = y, ["r"] = r });
            }

            foreach (JsonObject l in Items(a, "lines"))
            {
                double x1 = GetDouble(l, "x1", 0), y1 = GetDouble(l, "y1", 0);
                double x2 = GetDouble(l, "x2", 0), y2 = GetDouble(l, "y2", 0);
                place(new Line(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0)), l);
                drawn.Add(new JsonObject
                {
                    ["type"] = "Line",
                    ["length"] = Math.Round(Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)), 4)
                });
            }

            foreach (JsonObject ar in Items(a, "arcs"))
            {
                double r = GetDouble(ar, "r", 0);
                if (r <= 0) throw new InvalidOperationException("圆弧半径必须大于 0。");
                double x = GetDouble(ar, "x", 0), y = GetDouble(ar, "y", 0);
                // 入参用角度制（工程习惯），API 需要弧度
                double sa = GetDouble(ar, "start_angle", 0) * Math.PI / 180.0;
                double ea = GetDouble(ar, "end_angle", 90) * Math.PI / 180.0;
                place(new Arc(new Point3d(x, y, 0), r, sa, ea), ar);
                drawn.Add(new JsonObject { ["type"] = "Arc", ["x"] = x, ["y"] = y, ["r"] = r });
            }

            foreach (JsonObject p in Items(a, "polylines"))
            {
                var pts = p["points"] as JsonArray;
                if (pts == null || pts.Count < 2)
                    throw new InvalidOperationException("多段线至少需要 2 个点，格式 points:[[x,y],[x,y],...]。");
                var pl = new Polyline();
                double width = GetDouble(p, "width", 0);
                int i = 0;
                foreach (JsonNode pn in pts)
                {
                    var pair = pn as JsonArray;
                    if (pair == null || pair.Count < 2)
                        throw new InvalidOperationException("points 里每项必须是 [x, y]。");
                    pl.AddVertexAt(i++, new Point2d(
                        pair[0].GetValue<double>(), pair[1].GetValue<double>()), 0, width, width);
                }
                pl.Closed = GetBool(p, "closed", false);
                place(pl, p);
                drawn.Add(new JsonObject
                {
                    ["type"] = "Polyline",
                    ["vertices"] = i,
                    ["closed"] = pl.Closed,
                    ["length"] = Math.Round(pl.Length, 4)
                });
            }

            foreach (JsonObject t in Items(a, "texts"))
            {
                string content = GetString(t, "text", null);
                if (string.IsNullOrEmpty(content))
                    throw new InvalidOperationException("文字缺少 text 内容。");
                double h = GetDouble(t, "height", 2.5);
                if (h <= 0) throw new InvalidOperationException("文字高度必须大于 0。");
                var dbt = new DBText
                {
                    Position = new Point3d(GetDouble(t, "x", 0), GetDouble(t, "y", 0), 0),
                    Height = h,
                    TextString = content,
                    Rotation = GetDouble(t, "rotation", 0) * Math.PI / 180.0
                };
                // 与图内既有标注对齐要这两项：文字样式（中文字体）与宽度因子
                string sty = GetString(t, "style", null);
                if (!string.IsNullOrEmpty(sty))
                {
                    var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                    if (!tst.Has(sty))
                        throw new InvalidOperationException("图里没有文字样式 '" + sty + "'。");
                    dbt.TextStyleId = tst[sty];
                }
                double wf = GetDouble(t, "width_factor", 0);
                if (wf > 0) dbt.WidthFactor = wf;
                place(dbt, t);
                drawn.Add(new JsonObject { ["type"] = "Text", ["text"] = content, ["height"] = h });
            }

            foreach (JsonObject hz in Items(a, "hatches"))
            {
                var pts = hz["points"] as JsonArray;
                if (pts == null || pts.Count < 3)
                    throw new InvalidOperationException("填充需要 points:[[x,y],...] 至少 3 个顶点。");
                var hatch = new Hatch();
                // Hatch 的调用顺序有讲究：先入库，再定图案，再挂边界环，最后求值。
                ms.AppendEntity(hatch);
                tr.AddNewlyCreatedDBObject(hatch, true);
                hatch.PatternScale = Math.Max(GetDouble(hz, "scale", 1.0), 1e-6);
                hatch.SetHatchPattern(HatchPatternType.PreDefined, GetString(hz, "pattern", "SOLID"));
                double hatchAngle = GetDouble(hz, "angle", 0);
                if (hatchAngle != 0) hatch.PatternAngle = hatchAngle * Math.PI / 180.0;
                var ringPts = new Point2dCollection();
                var ringBulges = new DoubleCollection();
                foreach (JsonNode pn in pts)
                {
                    var pair = pn as JsonArray;
                    if (pair == null || pair.Count < 2)
                        throw new InvalidOperationException("hatches.points 里每项必须是 [x, y]。");
                    ringPts.Add(new Point2d(pair[0].GetValue<double>(), pair[1].GetValue<double>()));
                    ringBulges.Add(0);
                }
                hatch.AppendLoop(HatchLoopTypes.Default, ringPts, ringBulges);
                hatch.EvaluateHatch(true);
                string hatchLayer = GetString(hz, "layer", defaultLayer);
                if (!string.IsNullOrEmpty(hatchLayer) && hatchLayer != "0") hatch.Layer = hatchLayer;
                double hatchColor = GetDouble(hz, "color", double.NaN);
                if (!double.IsNaN(hatchColor))
                    hatch.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                        Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)hatchColor);
                drawn.Add(new JsonObject
                {
                    ["type"] = "Hatch",
                    ["pattern"] = hatch.PatternName,
                    ["vertices"] = ringPts.Count
                });
            }
        }

        // 取 a[key] 数组里的 JsonObject 项（缺失/空则返回空序列）
        static IEnumerable<JsonObject> Items(JsonObject a, string key)
        {
            var arr = a[key] as JsonArray;
            if (arr == null) yield break;
            foreach (JsonNode n in arr)
            {
                var o = n as JsonObject;
                if (o != null) yield return o;
            }
        }

        // ---------- 块 ----------

        static JsonNode ListBlocks(JsonObject a, Document doc)
        {
            bool includeLayout = GetBool(a, "include_layout", false);
            Database db = doc.Database;
            var arr = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // 先统计每个块定义被插入了多少次
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    string nm = EffectiveBlockName(tr, br);
                    counts.TryGetValue(nm, out int c);
                    counts[nm] = c + 1;
                }

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId btrId in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                    if (!includeLayout && (btr.IsLayout || btr.IsAnonymous)) continue;

                    var tags = new JsonArray();
                    if (btr.HasAttributeDefinitions)
                    {
                        foreach (ObjectId eid in btr)
                        {
                            var ad = tr.GetObject(eid, OpenMode.ForRead) as AttributeDefinition;
                            if (ad != null && !ad.Constant) tags.Add(ad.Tag);
                        }
                    }
                    counts.TryGetValue(btr.Name, out int used);
                    arr.Add(new JsonObject
                    {
                        ["name"] = btr.Name,
                        ["has_attributes"] = btr.HasAttributeDefinitions,
                        ["attribute_tags"] = tags,
                        ["inserted"] = used
                    });
                }
                tr.Commit();
            }
            return arr;
        }

        // 图框常常插在**布局**里，只扫模型空间会得出"图里没有图框"的错误结论。
        // 所以这里把模型空间和每个布局都当作一个"空间"统一遍历。
        static List<KeyValuePair<string, ObjectId>> Spaces(Database db, Transaction tr, string which)
        {
            var list = new List<KeyValuePair<string, ObjectId>>();
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            bool wantModel = !string.Equals(which, "layout", StringComparison.OrdinalIgnoreCase);
            bool wantLayout = !string.Equals(which, "model", StringComparison.OrdinalIgnoreCase);

            if (wantModel)
                list.Add(new KeyValuePair<string, ObjectId>("Model", bt[BlockTableRecord.ModelSpace]));
            if (wantLayout)
            {
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry e in dict)
                {
                    var lo = tr.GetObject(e.Value, OpenMode.ForRead) as Layout;
                    if (lo == null || lo.ModelType) continue;
                    list.Add(new KeyValuePair<string, ObjectId>(lo.LayoutName, lo.BlockTableRecordId));
                }
            }
            return list;
        }

        // 列出块引用及其包围盒（模型空间 + 各布局）。找"每个图框在哪、左下角在哪"就靠它。
        static JsonNode BlockRefs(JsonObject a, Document doc)
        {
            string filter = GetString(a, "name", null);
            string which = GetString(a, "space", "all");
            int max = (int)GetDouble(a, "max", 200);
            Database db = doc.Database;
            var arr = new JsonArray();
            int total = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
              foreach (var sp in Spaces(db, tr, which))
              {
                var btrSpace = (BlockTableRecord)tr.GetObject(sp.Value, OpenMode.ForRead);
                foreach (ObjectId id in btrSpace)
                {
                    var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    string nm = EffectiveBlockName(tr, br);
                    if (!string.IsNullOrEmpty(filter) &&
                        nm.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    total++;
                    if (arr.Count >= max) continue;

                    var o = new JsonObject
                    {
                        ["name"] = nm,
                        ["space"] = sp.Key,
                        ["layer"] = br.Layer,
                        ["handle"] = br.Handle.ToString(),
                        ["x"] = Math.Round(br.Position.X, 3),
                        ["y"] = Math.Round(br.Position.Y, 3),
                        ["rotation"] = Math.Round(br.Rotation * 180.0 / Math.PI, 3),
                        ["scale_x"] = Math.Round(br.ScaleFactors.X, 6),
                        ["scale_y"] = Math.Round(br.ScaleFactors.Y, 6)
                    };
                    // 空块/退化块取包围盒会抛 eNullExtents，记下原因继续，不拖垮整批
                    try
                    {
                        Extents3d ex = br.GeometricExtents;
                        o["minx"] = Math.Round(ex.MinPoint.X, 3);
                        o["miny"] = Math.Round(ex.MinPoint.Y, 3);
                        o["maxx"] = Math.Round(ex.MaxPoint.X, 3);
                        o["maxy"] = Math.Round(ex.MaxPoint.Y, 3);
                        o["width"] = Math.Round(ex.MaxPoint.X - ex.MinPoint.X, 3);
                        o["height"] = Math.Round(ex.MaxPoint.Y - ex.MinPoint.Y, 3);
                    }
                    catch (System.Exception ex) { o["extents_error"] = ex.Message; }

                    arr.Add(o);
                }
              }
              tr.Commit();
            }
            return new JsonObject { ["count"] = total, ["returned"] = arr.Count, ["refs"] = arr };
        }

        // JsonNode → 字符串。字符串节点取原值，数字/布尔取其 JSON 文本，null 归空串。
        static string NodeToStr(JsonNode n)
        {
            if (n == null) return "";
            try { return n.GetValue<string>(); } catch { return n.ToString(); }
        }

        // 导出块引用的属性（图签图框的唯一真源）。只读，不改图。
        // 出 JSON 是为了让人改完再由 set_block_attributes 原样反写——句柄是两边对齐的锚点。
        static JsonNode DumpBlockAttributes(JsonObject a, Document doc)
        {
            string filter = GetString(a, "name", null);
            string which = GetString(a, "space", "all");
            int max = (int)GetDouble(a, "max", 500);
            bool onlyAttr = GetBool(a, "with_attributes_only", true);
            string outPath = GetString(a, "out", null);

            Database db = doc.Database;
            var arr = new JsonArray();
            int total = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
              foreach (var sp in Spaces(db, tr, which))
              {
                var btrSpace = (BlockTableRecord)tr.GetObject(sp.Value, OpenMode.ForRead);
                foreach (ObjectId id in btrSpace)
                {
                    var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    string nm = EffectiveBlockName(tr, br);
                    if (!string.IsNullOrEmpty(filter) &&
                        nm.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var dict = new JsonObject();
                    var list = new JsonArray();
                    foreach (ObjectId attId in br.AttributeCollection)
                    {
                        var att = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                        if (att == null) continue;
                        string tag = att.Tag ?? "";
                        string val = att.TextString ?? "";
                        dict[tag] = val;
                        list.Add(new JsonObject
                        {
                            ["tag"] = tag,
                            ["value"] = val,
                            ["handle"] = att.Handle.ToString(),
                            ["is_mtext"] = att.IsMTextAttribute
                        });
                    }
                    if (onlyAttr && list.Count == 0) continue;

                    total++;
                    if (arr.Count >= max) continue;

                    arr.Add(new JsonObject
                    {
                        ["handle"] = br.Handle.ToString(),
                        ["name"] = nm,
                        ["space"] = sp.Key,
                        ["layer"] = br.Layer,
                        ["x"] = Math.Round(br.Position.X, 3),
                        ["y"] = Math.Round(br.Position.Y, 3),
                        ["attributes"] = dict,
                        ["attribute_list"] = list
                    });
                }
              }
              tr.Commit();
            }

            var result = new JsonObject { ["count"] = total, ["returned"] = arr.Count, ["blocks"] = arr };
            if (!string.IsNullOrEmpty(outPath))
            {
                if (!Path.IsPathRooted(outPath))
                    throw new InvalidOperationException("out 必须是绝对路径：" + outPath);
                string dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var opts = new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                File.WriteAllText(outPath, result.ToJsonString(opts), new System.Text.UTF8Encoding(false));
                result["out"] = outPath;
            }
            return result;
        }

        // 按 JSON 反写块属性。句柄命中优先；没给句柄就按块名 + attributes 批量改同一批标签。
        // 值相同的跳过（记 unchanged），图里没有的标签记进 missing——图签少一个标签不该把整批打死。
        static JsonNode SetBlockAttributes(JsonObject a, Document doc)
        {
            string fromPath = GetString(a, "from", null);
            string filter = GetString(a, "name", null);
            string which = GetString(a, "space", "all");
            bool missingOk = GetBool(a, "missing_ok", true);

            var byHandle = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Action<JsonArray> take = items =>
            {
                if (items == null) return;
                foreach (var it in items)
                {
                    var o = it as JsonObject;
                    if (o == null) continue;
                    string h = GetString(o, "handle", null);
                    var attrs = o["attributes"] as JsonObject;
                    if (string.IsNullOrEmpty(h) || attrs == null) continue;
                    Dictionary<string, string> d;
                    if (!byHandle.TryGetValue(h, out d))
                    {
                        d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        byHandle[h] = d;
                    }
                    foreach (var kv in attrs) d[kv.Key] = NodeToStr(kv.Value);
                }
            };

            take(a["items"] as JsonArray);
            if (!string.IsNullOrEmpty(fromPath))
            {
                if (!File.Exists(fromPath))
                    throw new InvalidOperationException("from 文件不存在：" + fromPath);
                var parsed = JsonNode.Parse(File.ReadAllText(fromPath)) as JsonObject;
                if (parsed != null) take(parsed["blocks"] as JsonArray);
            }

            var bulk = a["attributes"] as JsonObject;

            // 宽度因子：按标签给，作用范围与 attributes 批量改一致（name 过滤 + 逐句柄命中）。
            // 可以单独给（不改值只压字），所以下面的"至少给一样"判断要把它算进去。
            var widths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var wfObj = a["width_factors"] as JsonObject;
            if (wfObj != null)
                foreach (var kv in wfObj)
                {
                    double f = GetDouble(wfObj, kv.Key, double.NaN);
                    if (double.IsNaN(f) || f <= 0)
                        throw new InvalidOperationException(
                            "width_factors['" + kv.Key + "'] 必须是正数，收到：" + NodeToStr(kv.Value));
                    widths[kv.Key] = f;
                }

            if (byHandle.Count == 0 && bulk == null && widths.Count == 0)
                throw new InvalidOperationException(
                    "要么给 items/from（逐句柄回填），要么给 attributes（按 name 批量改），要么给 width_factors（只压字宽）。");

            Database db = doc.Database;
            int blocksTouched = 0, attrsSet = 0, unchanged = 0, widthSet = 0, widthUnchanged = 0;
            var missing = new JsonArray();
            var touched = new JsonArray();
            var mtextSkipped = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
              foreach (var sp in Spaces(db, tr, which))
              {
                var btrSpace = (BlockTableRecord)tr.GetObject(sp.Value, OpenMode.ForRead);
                foreach (ObjectId id in btrSpace)
                {
                    var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    string h = br.Handle.ToString();
                    string nm = EffectiveBlockName(tr, br);

                    Dictionary<string, string> want = null;
                    bool inScope = false;
                    if (byHandle.ContainsKey(h)) { want = byHandle[h]; inScope = true; }
                    else if (bulk != null || widths.Count > 0)
                    {
                        if (!string.IsNullOrEmpty(filter) &&
                            nm.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        inScope = true;
                        if (bulk != null)
                        {
                            want = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var kv in bulk) want[kv.Key] = NodeToStr(kv.Value);
                        }
                    }
                    // 只给 width_factors 时 want 为空也要往下走——那趟只压字宽不改值
                    if (!inScope) continue;
                    if ((want == null || want.Count == 0) && widths.Count == 0) continue;

                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int setHere = 0;
                    foreach (ObjectId attId in br.AttributeCollection)
                    {
                        var att = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                        if (att == null) continue;
                        string tag = att.Tag ?? "";
                        seen.Add(tag);

                        string v;
                        if (want != null && want.TryGetValue(tag, out v))
                        {
                            if (string.Equals(att.TextString ?? "", v, StringComparison.Ordinal)) unchanged++;
                            else
                            {
                                att.UpgradeOpen();
                                att.TextString = v;
                                // 多行属性的 MText 内容不跟 TextString 自动同步，得显式刷一次
                                if (att.IsMTextAttribute) { try { att.UpdateMTextAttribute(); } catch { } }
                                att.DowngradeOpen();
                                attrsSet++; setHere++;
                            }
                        }

                        double wf;
                        if (widths.TryGetValue(tag, out wf))
                        {
                            // 多行属性走 MText 排版，WidthFactor 写了也不生效——记名单，别安静地成功
                            if (att.IsMTextAttribute)
                                mtextSkipped.Add(new JsonObject
                                { ["handle"] = h, ["block"] = nm, ["tag"] = tag });
                            else if (Math.Abs(att.WidthFactor - wf) < 1e-9) widthUnchanged++;
                            else
                            {
                                att.UpgradeOpen();
                                att.WidthFactor = wf;
                                att.DowngradeOpen();
                                widthSet++; setHere++;
                            }
                        }
                    }
                    var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (want != null) foreach (var kv in want) expected.Add(kv.Key);
                    foreach (var kv in widths) expected.Add(kv.Key);
                    foreach (string tagWanted in expected)
                        if (!seen.Contains(tagWanted))
                            missing.Add(new JsonObject { ["handle"] = h, ["block"] = nm, ["tag"] = tagWanted });

                    if (setHere > 0)
                    {
                        blocksTouched++;
                        touched.Add(new JsonObject
                        {
                            ["handle"] = h, ["block"] = nm, ["space"] = sp.Key, ["set"] = setHere
                        });
                    }
                }
              }
              tr.Commit();
            }

            if (missing.Count > 0 && !missingOk)
                throw new InvalidOperationException("这些标签在图里不存在：" + missing.ToJsonString());

            return new JsonObject
            {
                ["blocks_touched"] = blocksTouched,
                ["attributes_set"] = attrsSet,
                ["unchanged"] = unchanged,
                ["width_set"] = widthSet,
                ["width_unchanged"] = widthUnchanged,
                ["mtext_skipped_count"] = mtextSkipped.Count,
                ["mtext_skipped"] = mtextSkipped,
                ["missing_count"] = missing.Count,
                ["missing"] = missing,
                ["touched"] = touched
            };
        }

        // 模型空间总包围盒。整体插块时块的基点取源图 INSBASE，所以一并报出来。
        static JsonNode ModelExtents(JsonObject a, Document doc)
        {
            string layerFilter = GetString(a, "layer", null);
            Database db = doc.Database;
            bool any = false;
            double minx = 0, miny = 0, maxx = 0, maxy = 0;
            int counted = 0, skipped = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    if (!string.IsNullOrEmpty(layerFilter) &&
                        ent.Layer.IndexOf(layerFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Extents3d ex;
                    try { ex = ent.GeometricExtents; } catch { skipped++; continue; }
                    if (!any)
                    {
                        minx = ex.MinPoint.X; miny = ex.MinPoint.Y;
                        maxx = ex.MaxPoint.X; maxy = ex.MaxPoint.Y; any = true;
                    }
                    else
                    {
                        minx = Math.Min(minx, ex.MinPoint.X); miny = Math.Min(miny, ex.MinPoint.Y);
                        maxx = Math.Max(maxx, ex.MaxPoint.X); maxy = Math.Max(maxy, ex.MaxPoint.Y);
                    }
                    counted++;
                }
                tr.Commit();
            }
            if (!any)
                throw new InvalidOperationException("模型空间没有可取包围盒的实体（图层筛选过严？）");

            return new JsonObject
            {
                ["entities_counted"] = counted,
                ["entities_skipped"] = skipped,
                ["minx"] = Math.Round(minx, 3),
                ["miny"] = Math.Round(miny, 3),
                ["maxx"] = Math.Round(maxx, 3),
                ["maxy"] = Math.Round(maxy, 3),
                ["width"] = Math.Round(maxx - minx, 3),
                ["height"] = Math.Round(maxy - miny, 3),
                ["insbase_x"] = Math.Round(db.Insbase.X, 3),
                ["insbase_y"] = Math.Round(db.Insbase.Y, 3)
            };
        }

        static JsonNode ListMarkedRegions(JsonObject a, Document doc)
        {
            bool markedOnly = GetBool(a, "marked_only", true);
            string layerFilter = GetString(a, "layer", null);
            int max = (int)GetDouble(a, "max", 100);
            var rows = new JsonArray();
            int candidates = 0;

            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(doc.Database, tr))
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (ent == null) continue;
                    bool supported = ent is Polyline || ent is Polyline2d || ent is Polyline3d || ent is Circle;
                    if (!supported) continue;
                    if (layerFilter != null
                        && ent.Layer.IndexOf(layerFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    candidates++;

                    var xdata = ResultBufferJson(ent.XData);
                    var ext = ExtensionDictionaryJson(tr, ent);
                    var links = HyperlinksJson(ent);
                    bool marked = HasMeaningfulXData(xdata) || ext.Count > 0 || links.Count > 0;
                    if (markedOnly && !marked) continue;
                    if (rows.Count >= max) continue;

                    var row = new JsonObject
                    {
                        ["handle"] = ent.Handle.ToString(),
                        ["type"] = ent.GetType().Name,
                        ["layer"] = ent.Layer,
                        ["closed"] = IsClosedRegion(ent),
                        ["marked"] = marked,
                        ["xdata"] = xdata,
                        ["extension_dictionary"] = ext,
                        ["hyperlinks"] = links,
                        ["vertices"] = RegionVerticesJson(tr, ent)
                    };
                    try
                    {
                        Extents3d ex = ent.GeometricExtents;
                        row["extents"] = new JsonObject
                        {
                            ["minx"] = ex.MinPoint.X, ["miny"] = ex.MinPoint.Y,
                            ["maxx"] = ex.MaxPoint.X, ["maxy"] = ex.MaxPoint.Y,
                            ["width"] = ex.MaxPoint.X - ex.MinPoint.X,
                            ["height"] = ex.MaxPoint.Y - ex.MinPoint.Y
                        };
                    }
                    catch { }
                    try
                    {
                        if (ent is Polyline pl) row["area"] = pl.Area;
                        else if (ent is Circle c) row["area"] = Math.PI * c.Radius * c.Radius;
                    }
                    catch { }
                    rows.Add(row);
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["candidate_count"] = candidates,
                ["returned_count"] = rows.Count,
                ["marked_only"] = markedOnly,
                ["regions"] = rows
            };
        }

        static JsonNode ListCivilViews(JsonObject a, Document doc)
        {
            string filter = (GetString(a, "type", "all") ?? "all").Trim().ToLowerInvariant();
            int max = (int)GetDouble(a, "max", 1000);
            var rows = new JsonArray();
            int sectionCount = 0, profileCount = 0, extentsFailed = 0;

            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ModelSpace(doc.Database, tr))
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (ent == null) continue;
                    string typeName = ent.GetType().Name;
                    bool isSection = typeName.Equals("SectionView", StringComparison.OrdinalIgnoreCase);
                    bool isProfile = typeName.Equals("ProfileView", StringComparison.OrdinalIgnoreCase);
                    if (!isSection && !isProfile) continue;
                    if (isSection) sectionCount++; else profileCount++;
                    if (filter == "section" && !isSection) continue;
                    if (filter == "profile" && !isProfile) continue;
                    if (rows.Count >= max) continue;

                    var row = new JsonObject
                    {
                        ["handle"] = ent.Handle.ToString(),
                        ["type"] = typeName,
                        ["layer"] = ent.Layer
                    };
                    try
                    {
                        var p = ent.GetType().GetProperty("Name");
                        if (p != null) row["name"] = Convert.ToString(p.GetValue(ent, null));
                    }
                    catch { }
                    try
                    {
                        Extents3d ex = ent.GeometricExtents;
                        row["extents"] = new JsonObject
                        {
                            ["minx"] = ex.MinPoint.X, ["miny"] = ex.MinPoint.Y,
                            ["maxx"] = ex.MaxPoint.X, ["maxy"] = ex.MaxPoint.Y,
                            ["width"] = ex.MaxPoint.X - ex.MinPoint.X,
                            ["height"] = ex.MaxPoint.Y - ex.MinPoint.Y
                        };
                    }
                    catch
                    {
                        extentsFailed++;
                    }
                    rows.Add(row);
                }
                tr.Commit();
            }

            return new JsonObject
            {
                ["section_view_count"] = sectionCount,
                ["profile_view_count"] = profileCount,
                ["returned_count"] = rows.Count,
                ["extents_failed"] = extentsFailed,
                ["views"] = rows
            };
        }

        static JsonNode ProfileLabelSetsDump(JsonObject a, Document doc)
        {
            string filter = GetString(a, "name", null);
            var result = new JsonArray();
            CivDoc civ = Civ(doc.Database);
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civ.Styles.LabelSetStyles.ProfileLabelSetStyles)
                {
                    var style = tr.GetObject(id, OpenMode.ForRead)
                        as Autodesk.Civil.DatabaseServices.Styles.ProfileLabelSetStyle;
                    if (style == null) continue;
                    if (!string.IsNullOrEmpty(filter)
                        && style.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var items = new JsonArray();
                    foreach (Autodesk.Civil.DatabaseServices.Styles.ProfileLabelSetItem item in style)
                    {
                        items.Add(new JsonObject
                        {
                            ["label_style_type"] = item.LabelStyleType.ToString(),
                            ["label_style_name"] = item.LabelStyleName,
                            ["label_style_id"] = item.LabelStyleId.Handle.ToString()
                        });
                    }
                    result.Add(new JsonObject
                    {
                        ["name"] = style.Name,
                        ["item_count"] = items.Count,
                        ["items"] = items
                    });
                }
                tr.Commit();
            }
            return result;
        }

        static JsonNode SetModelView(JsonObject a, Document doc)
        {
            if (a["minx"] == null || a["miny"] == null || a["maxx"] == null || a["maxy"] == null)
                throw new InvalidOperationException("set_model_view 需要 minx/miny/maxx/maxy。");
            double minx = GetDouble(a, "minx", 0);
            double miny = GetDouble(a, "miny", 0);
            double maxx = GetDouble(a, "maxx", 0);
            double maxy = GetDouble(a, "maxy", 0);
            if (maxx <= minx || maxy <= miny)
                throw new InvalidOperationException("set_model_view 的窗口范围无效。");

            try
            {
                var lm = LayoutManager.Current;
                if (!string.Equals(lm.CurrentLayout, "Model", StringComparison.OrdinalIgnoreCase))
                    lm.CurrentLayout = "Model";
            }
            catch { }

            using (var view = doc.Editor.GetCurrentView())
            {
                view.CenterPoint = new Point2d((minx + maxx) / 2.0, (miny + maxy) / 2.0);
                view.Width = maxx - minx;
                view.Height = maxy - miny;
                doc.Editor.SetCurrentView(view);
            }

            return new JsonObject
            {
                ["minx"] = minx, ["miny"] = miny,
                ["maxx"] = maxx, ["maxy"] = maxy,
                ["center_x"] = (minx + maxx) / 2.0,
                ["center_y"] = (miny + maxy) / 2.0,
                ["width"] = maxx - minx,
                ["height"] = maxy - miny
            };
        }

        static bool IsClosedRegion(Entity ent)
        {
            if (ent is Polyline pl) return pl.Closed;
            if (ent is Polyline2d pl2) return pl2.Closed;
            if (ent is Polyline3d pl3) return pl3.Closed;
            return ent is Circle;
        }

        static JsonArray RegionVerticesJson(Transaction tr, Entity ent)
        {
            var arr = new JsonArray();
            if (ent is Polyline pl)
            {
                for (int i = 0; i < pl.NumberOfVertices; i++)
                {
                    Point2d p = pl.GetPoint2dAt(i);
                    arr.Add(new JsonArray(p.X, p.Y));
                }
            }
            else if (ent is Polyline2d pl2)
            {
                foreach (ObjectId vid in pl2)
                {
                    var v = tr.GetObject(vid, OpenMode.ForRead) as Vertex2d;
                    if (v != null) arr.Add(new JsonArray(v.Position.X, v.Position.Y, v.Position.Z));
                }
            }
            else if (ent is Polyline3d pl3)
            {
                foreach (ObjectId vid in pl3)
                {
                    var v = tr.GetObject(vid, OpenMode.ForRead) as PolylineVertex3d;
                    if (v != null) arr.Add(new JsonArray(v.Position.X, v.Position.Y, v.Position.Z));
                }
            }
            else if (ent is Circle c)
            {
                arr.Add(new JsonObject
                {
                    ["center"] = new JsonArray(c.Center.X, c.Center.Y, c.Center.Z),
                    ["radius"] = c.Radius
                });
            }
            return arr;
        }

        static JsonArray ResultBufferJson(ResultBuffer rb)
        {
            var arr = new JsonArray();
            if (rb == null) return arr;
            try
            {
                foreach (TypedValue tv in rb)
                    arr.Add(new JsonObject
                    {
                        ["code"] = tv.TypeCode,
                        ["value"] = tv.Value == null ? "" : Convert.ToString(tv.Value, CultureInfo.InvariantCulture)
                    });
            }
            catch { }
            return arr;
        }

        static bool HasMeaningfulXData(JsonArray xdata)
        {
            foreach (JsonNode n in xdata)
            {
                var o = n as JsonObject;
                if (o == null || o["code"] == null || o["value"] == null) continue;
                int code;
                if (!int.TryParse(o["code"].ToString(), out code) || code != 1001) continue;
                string app = o["value"].ToString();
                if (!string.Equals(app, "AeccUiBase", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        static JsonObject ExtensionDictionaryJson(Transaction tr, Entity ent)
        {
            var result = new JsonObject();
            if (ent.ExtensionDictionary.IsNull) return result;
            try
            {
                var dict = tr.GetObject(ent.ExtensionDictionary, OpenMode.ForRead) as DBDictionary;
                if (dict == null) return result;
                foreach (DBDictionaryEntry de in dict)
                {
                    var value = new JsonObject { ["type"] = de.Value.ObjectClass.Name };
                    try
                    {
                        var obj = tr.GetObject(de.Value, OpenMode.ForRead);
                        var xr = obj as Xrecord;
                        if (xr != null) value["data"] = ResultBufferJson(xr.Data);
                        else if (obj != null) value["properties"] = DumpShallow(tr, obj, 2);
                    }
                    catch { }
                    result[de.Key] = value;
                }
            }
            catch (System.Exception ex)
            {
                result["_error"] = ex.GetType().Name + ": " + Truncate(ex.Message, 120);
            }
            return result;
        }

        static JsonArray HyperlinksJson(Entity ent)
        {
            var arr = new JsonArray();
            try
            {
                var p = ent.GetType().GetProperty("Hyperlinks");
                var links = p == null ? null : p.GetValue(ent, null) as System.Collections.IEnumerable;
                if (links == null) return arr;
                foreach (object link in links)
                {
                    var row = new JsonObject();
                    foreach (string name in new[] { "Name", "Description", "DisplayString" })
                    {
                        try
                        {
                            var lp = link.GetType().GetProperty(name);
                            if (lp != null) row[name.ToLowerInvariant()] =
                                Convert.ToString(lp.GetValue(link, null), CultureInfo.InvariantCulture);
                        }
                        catch { }
                    }
                    arr.Add(row);
                }
            }
            catch { }
            return arr;
        }

        static string EffectiveBlockName(Transaction tr, BlockReference br)
        {
            try
            {
                ObjectId defId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                return ((BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead)).Name;
            }
            catch { return "(未知)"; }
        }

        // 按名字找布局的块表记录（图框在布局里时，插块必须插进对应布局）
        static ObjectId LayoutBtr(Database db, Transaction tr, string layoutName)
        {
            var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            var names = new List<string>();
            foreach (DBDictionaryEntry e in dict)
            {
                var lo = tr.GetObject(e.Value, OpenMode.ForRead) as Layout;
                if (lo == null) continue;
                if (string.Equals(lo.LayoutName, layoutName, StringComparison.OrdinalIgnoreCase))
                    return lo.BlockTableRecordId;
                names.Add(lo.LayoutName);
            }
            throw new InvalidOperationException(
                "图里没有名为 '" + layoutName + "' 的布局。现有：" + string.Join(" / ", names));
        }

        // 插入块引用；from_dwg 给了就先把那个 dwg 作为块定义导入（同名则重定义）
        static void InsertBlocks(Database db, Transaction tr, BlockTableRecord ms,
                                 JsonObject a, string defaultLayer, JsonArray drawn)
        {
            string defaultSpace = GetString(a, "space", null);
            foreach (JsonObject b in Items(a, "blocks"))
            {
                string name = GetString(b, "name", null);
                if (string.IsNullOrEmpty(name))
                    throw new InvalidOperationException("插块缺少 name。");
                string fromDwg = GetString(b, "from_dwg", null);
                // source_block：从块库文件里按名字取某个块定义（图框库的常见形态）。
                // 不给则退回“整个 dwg 当作一个块导入”。
                string srcBlock = GetString(b, "source_block", null);

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                ObjectId btrId;

                if (!string.IsNullOrEmpty(fromDwg))
                {
                    if (!File.Exists(fromDwg))
                        throw new InvalidOperationException("找不到块源文件：" + fromDwg);
                    using (Database src = new Database(false, true))
                    {
                        src.ReadDwgFile(fromDwg, FileOpenMode.OpenForReadAndAllShare, true, null);
                        src.CloseInput(true);

                        if (!string.IsNullOrEmpty(srcBlock))
                        {
                            // 克隆指定的块定义（连同它的属性定义一起过来）
                            using (Transaction stx = src.TransactionManager.StartTransaction())
                            {
                                BlockTable sbt = (BlockTable)stx.GetObject(src.BlockTableId, OpenMode.ForRead);
                                if (!sbt.Has(srcBlock))
                                    throw new InvalidOperationException(
                                        "块源文件里没有块定义 '" + srcBlock + "'：" + fromDwg +
                                        "（先对该文件跑 list_blocks 查名称）");
                                var ids = new ObjectIdCollection();
                                ids.Add(sbt[srcBlock]);
                                var map = new IdMapping();
                                db.WblockCloneObjects(ids, db.BlockTableId, map,
                                                      DuplicateRecordCloning.Replace, false);
                                stx.Commit();
                            }
                            // 克隆后块名沿用源名；name 若不同，以 name 为准去查（通常两者一致）
                            BlockTable bt2 = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                            string lookup = bt2.Has(name) ? name : srcBlock;
                            if (!bt2.Has(lookup))
                                throw new InvalidOperationException("克隆块定义后仍找不到：" + lookup);
                            btrId = bt2[lookup];
                        }
                        else btrId = db.Insert(name, src, false);   // 整个 dwg 作为一个块定义导入
                    }
                }
                else
                {
                    if (!bt.Has(name))
                        throw new InvalidOperationException(
                            "图中没有名为 '" + name + "' 的块定义（可用 from_dwg 从外部文件导入，或先 list_blocks 查名称）。");
                    btrId = bt[name];
                }

                double x = GetDouble(b, "x", 0), y = GetDouble(b, "y", 0);
                double scale = GetDouble(b, "scale", 1.0);
                if (scale <= 0) throw new InvalidOperationException("块比例必须大于 0。");
                double rot = GetDouble(b, "rotation", 0) * Math.PI / 180.0;

                var br = new BlockReference(new Point3d(x, y, 0), btrId)
                {
                    ScaleFactors = new Scale3d(scale),
                    Rotation = rot
                };

                // space：缺省插模型空间；给布局名就插进那个布局（图框在布局里时必须这样）
                string space = GetString(b, "space", defaultSpace);
                BlockTableRecord dest = ms;
                if (!string.IsNullOrEmpty(space) &&
                    !string.Equals(space, "Model", StringComparison.OrdinalIgnoreCase))
                    dest = (BlockTableRecord)tr.GetObject(LayoutBtr(db, tr, space), OpenMode.ForWrite);

                dest.AppendEntity(br);
                tr.AddNewlyCreatedDBObject(br, true);
                string lay = GetString(b, "layer", defaultLayer);
                if (!string.IsNullOrEmpty(lay) && lay != "0") br.Layer = lay;

                // 填块属性（图框的图号/图名就靠这个）
                var wanted = b["attributes"] as JsonObject;
                int filled = 0;
                var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                if (btr.HasAttributeDefinitions)
                {
                    foreach (ObjectId eid in btr)
                    {
                        var ad = tr.GetObject(eid, OpenMode.ForRead) as AttributeDefinition;
                        if (ad == null || ad.Constant) continue;
                        var ar = new AttributeReference();
                        ar.SetAttributeFromBlock(ad, br.BlockTransform);
                        if (wanted != null && wanted[ad.Tag] != null)
                        {
                            ar.TextString = wanted[ad.Tag].ToString();
                            filled++;
                        }
                        br.AttributeCollection.AppendAttribute(ar);
                        tr.AddNewlyCreatedDBObject(ar, true);
                    }
                }

                drawn.Add(new JsonObject
                {
                    ["type"] = "BlockReference",
                    ["name"] = name,
                    ["space"] = string.IsNullOrEmpty(space) ? "Model" : space,
                    ["x"] = x,
                    ["y"] = y,
                    ["scale"] = scale,
                    ["attributes_filled"] = filled,
                    ["imported_from"] = fromDwg ?? "(图内已有)"
                });
            }
        }

        // ---------- 改动既有图纸（默认副本预演） ----------

        static JsonNode ModifyDwg(JsonObject a, Document doc)
        {
            string target = GetString(a, "dwg", null);
            if (string.IsNullOrEmpty(target))
                throw new InvalidOperationException("缺少参数 dwg（要改动的图纸路径）。");
            if (!Path.IsPathRooted(target))
                throw new InvalidOperationException("dwg 必须是绝对路径：" + target);
            if (!File.Exists(target))
                throw new InvalidOperationException("找不到图纸：" + target);

            bool apply = GetBool(a, "apply", false);      // 默认预演，绝不擅自改原图
            bool backup = GetBool(a, "backup", true);
            string layer = GetString(a, "layer", "0");
            var drawn = new JsonArray();
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            // 预演输出路径
            string outPath;
            if (apply) outPath = target;
            else
            {
                outPath = GetString(a, "out", null);
                if (string.IsNullOrEmpty(outPath))
                {
                    string dir = Path.GetDirectoryName(target);
                    string stem = Path.GetFileNameWithoutExtension(target);
                    outPath = Path.Combine(dir, stem + "_预演_" + stamp + ".dwg");
                }
            }

            string backupPath = null;
            if (apply && backup)
            {
                string dir = Path.GetDirectoryName(target);
                string stem = Path.GetFileNameWithoutExtension(target);
                backupPath = Path.Combine(dir, stem + "_备份_" + stamp + ".dwg");
                File.Copy(target, backupPath, false);      // 备份失败就让它抛，不带伤前进
            }

            using (Database db = new Database(false, true))
            {
                db.ReadDwgFile(target, FileOpenMode.OpenForReadAndAllShare, true, null);
                db.CloseInput(true);

                // 外参先挂（AttachXref 不喜欢在已开事务里跑）；同名已存在就跳过，可重跑
                foreach (JsonObject xr in Items(a, "xrefs"))
                {
                    string xrPath = GetString(xr, "path", null);
                    if (string.IsNullOrEmpty(xrPath) || !Path.IsPathRooted(xrPath))
                        throw new InvalidOperationException("xrefs.path 必须是绝对路径：" + (xrPath ?? "(空)"));
                    if (!File.Exists(xrPath))
                        throw new InvalidOperationException("外参文件不存在：" + xrPath);
                    string xrName = GetString(xr, "name", Path.GetFileNameWithoutExtension(xrPath));
                    bool already;
                    using (Transaction ck = db.TransactionManager.StartTransaction())
                    {
                        var ckBt = (BlockTable)ck.GetObject(db.BlockTableId, OpenMode.ForRead);
                        already = ckBt.Has(xrName);
                        ck.Commit();
                    }
                    if (already)
                    {
                        drawn.Add(new JsonObject
                        { ["type"] = "Xref", ["name"] = xrName, ["skipped"] = "同名块/外参已存在" });
                        continue;
                    }
                    ObjectId xrDefId = GetBool(xr, "overlay", false)
                        ? db.OverlayXref(xrPath, xrName)
                        : db.AttachXref(xrPath, xrName);
                    using (Transaction xtr = db.TransactionManager.StartTransaction())
                    {
                        var xbt = (BlockTable)xtr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        var xms = (BlockTableRecord)xtr.GetObject(
                            xbt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                        var xbr = new BlockReference(new Point3d(
                            GetDouble(xr, "x", 0), GetDouble(xr, "y", 0), 0), xrDefId);
                        xms.AppendEntity(xbr);
                        xtr.AddNewlyCreatedDBObject(xbr, true);
                        string xrLayer = GetString(xr, "layer", null);
                        if (!string.IsNullOrEmpty(xrLayer))
                        {
                            EnsureLayers(db, xtr, new JsonArray(new JsonObject { ["name"] = xrLayer }));
                            xbr.Layer = xrLayer;
                        }
                        xtr.Commit();
                    }
                    drawn.Add(new JsonObject
                    {
                        ["type"] = "Xref",
                        ["name"] = xrName,
                        ["path"] = xrPath,
                        ["overlay"] = GetBool(xr, "overlay", false)
                    });
                }

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                        bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    // 全局 space：图元（线/文字/填充…）也能进指定布局；块另有逐项 space。
                    string spaceName = GetString(a, "space", null);
                    BlockTableRecord entDest = ms;
                    if (!string.IsNullOrEmpty(spaceName) &&
                        !string.Equals(spaceName, "Model", StringComparison.OrdinalIgnoreCase))
                        entDest = (BlockTableRecord)tr.GetObject(
                            LayoutBtr(db, tr, spaceName), OpenMode.ForWrite);

                    EnsureLayers(db, tr, a["layers"] as JsonArray);
                    AddEntities(db, tr, entDest, a, layer, drawn);
                    InsertBlocks(db, tr, ms, a, layer, drawn);
                    tr.Commit();
                }

                if (drawn.Count == 0)
                    throw new InvalidOperationException(
                        "没有任何改动内容——可用键：blocks / circles / lines / arcs / polylines / texts / hatches。");

                db.SaveAs(outPath, DwgVersion.Current);
            }

            return new JsonObject
            {
                ["mode"] = apply ? "已写回原图" : "副本预演（原图未改动）",
                ["source"] = target,
                ["output"] = outPath,
                ["backup"] = backupPath ?? (apply ? "(未备份)" : "(预演无需备份)"),
                ["added"] = drawn.Count,
                ["items"] = drawn,
                ["bytes"] = File.Exists(outPath) ? new FileInfo(outPath).Length : 0
            };
        }

        // ---------- 模型空间成图材料框 ----------
        static JsonNode CreateSheetRegion(JsonObject a, Document doc)
        {
            double x = GetDouble(a, "x", double.NaN);
            double y = GetDouble(a, "y", double.NaN);
            string alignment = GetString(a, "alignment", null);
            string anchor = "explicit";
            if (double.IsNaN(x) || double.IsNaN(y))
            {
                if (string.IsNullOrWhiteSpace(alignment))
                    throw new InvalidOperationException(
                        "材料框缺少左下角 x / y；也没有 alignment 可用于自动定位。");
                Database anchorDb = doc.Database;
                CivDoc anchorCiv = Civ(anchorDb);
                using (Transaction anchorTr = anchorDb.TransactionManager.StartTransaction())
                {
                    CivAlignment al = FindAlignment(anchorTr, anchorCiv, alignment);
                    if (al == null)
                        throw new InvalidOperationException("找不到路线 '" + alignment + "'。");
                    double e = 0, n = 0;
                    al.PointLocation(al.StartingStation, 0, ref e, ref n);
                    if (double.IsNaN(x)) x = e + GetDouble(a, "offset_x", -100);
                    if (double.IsNaN(y)) y = n + GetDouble(a, "offset_y", -1600);
                    anchor = "alignment_start:" + alignment;
                }
            }
            double scale = GetDouble(a, "scale", 500);
            if (scale <= 0) throw new InvalidOperationException("scale 必须大于 0。");
            string paper = GetString(a, "paper", "A3");
            string name = GetString(a, "name", "SHEET-01");
            string layerName = GetString(a, "layer", "C3DF-SHEET-REGION-NOPLOT");
            int count = Math.Max(1, (int)GetDouble(a, "count", 1));
            double pitchX = GetDouble(a, "pitch_x", 0);
            double pitchY = GetDouble(a, "pitch_y", 0);
            bool replaceExisting = GetBool(a, "replace_existing", true);

            double paperW, paperH;
            PaperSizeMm(paper, out paperW, out paperH);
            double modelW = paperW * scale / 1000.0;
            double modelH = paperH * scale / 1000.0;
            var windows = new JsonArray();
            int erased = 0;

            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!lt.Has(layerName))
                {
                    lt.UpgradeOpen();
                    var ltr = new LayerTableRecord
                    {
                        Name = layerName,
                        IsPlottable = false,
                        Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                            Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 8)
                    };
                    lt.Add(ltr);
                    tr.AddNewlyCreatedDBObject(ltr, true);
                }
                else
                {
                    var ltr = (LayerTableRecord)tr.GetObject(lt[layerName], OpenMode.ForWrite);
                    ltr.IsPlottable = false;
                }

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                for (int i = 0; i < count; i++)
                {
                    double xi = x + i * pitchX;
                    double yi = y + i * pitchY;
                    string itemName = count == 1 ? name : name + "-" + (i + 1).ToString("00");
                    if (replaceExisting)
                    {
                        var oldIds = new List<ObjectId>();
                        foreach (ObjectId oldId in ms) oldIds.Add(oldId);
                        foreach (ObjectId oldId in oldIds)
                        {
                            var oldEntity = tr.GetObject(oldId, OpenMode.ForRead, false) as Entity;
                            if (oldEntity == null || oldEntity.IsErased
                                || !string.Equals(oldEntity.Layer, layerName,
                                    StringComparison.OrdinalIgnoreCase)) continue;
                            bool same = false;
                            var oldText = oldEntity as DBText;
                            if (oldText != null)
                                same = oldText.TextString.StartsWith(itemName + "  ",
                                    StringComparison.Ordinal)
                                    && oldText.Position.DistanceTo(
                                        new Point3d(xi, yi + modelH, 0)) < 0.001;
                            var oldFrame = oldEntity as Polyline;
                            if (oldFrame != null && oldFrame.Closed && oldFrame.NumberOfVertices == 4)
                            {
                                try
                                {
                                    Extents3d ext = oldFrame.GeometricExtents;
                                    same = ext.MinPoint.DistanceTo(new Point3d(xi, yi, 0)) < 0.001
                                        && ext.MaxPoint.DistanceTo(
                                            new Point3d(xi + modelW, yi + modelH, 0)) < 0.001;
                                }
                                catch { }
                            }
                            if (!same) continue;
                            oldEntity.UpgradeOpen();
                            oldEntity.Erase();
                            erased++;
                        }
                    }
                    var pl = new Polyline(4) { Closed = true, Layer = layerName };
                    pl.AddVertexAt(0, new Point2d(xi, yi), 0, 0, 0);
                    pl.AddVertexAt(1, new Point2d(xi + modelW, yi), 0, 0, 0);
                    pl.AddVertexAt(2, new Point2d(xi + modelW, yi + modelH), 0, 0, 0);
                    pl.AddVertexAt(3, new Point2d(xi, yi + modelH), 0, 0, 0);
                    ms.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);

                    var label = new DBText
                    {
                        Position = new Point3d(xi, yi + modelH, 0),
                        Height = Math.Max(modelH / 80.0, 0.5),
                        TextString = itemName + "  " + paper + "  1:" +
                                     scale.ToString("0.##", CultureInfo.InvariantCulture),
                        Layer = layerName
                    };
                    ms.AppendEntity(label);
                    tr.AddNewlyCreatedDBObject(label, true);
                    windows.Add(new JsonObject
                    {
                        ["name"] = itemName,
                        ["minx"] = xi, ["miny"] = yi,
                        ["maxx"] = xi + modelW, ["maxy"] = yi + modelH,
                        ["width"] = modelW, ["height"] = modelH
                    });
                }
                tr.Commit();
            }

            var result = new JsonObject
            {
                ["name"] = name,
                ["paper"] = paper,
                ["scale"] = scale,
                ["paper_space_scale"] = 1000.0 / scale,
                ["layer"] = layerName,
                ["count"] = count,
                ["pitch_x"] = pitchX,
                ["pitch_y"] = pitchY,
                ["anchor"] = anchor,
                ["replace_existing"] = replaceExisting,
                ["old_entities_erased"] = erased,
                ["windows"] = windows
            };
            if (count == 1) result["window"] = windows[0].DeepClone();
            return result;
        }

        // ---------- 实体化布局图纸：模型空间材料直接复制到纸空间 ----------
        static JsonNode ComposeLayoutSheet(JsonObject a, Document doc)
        {
            string layoutName = GetString(a, "layout", "C3DF-A3-实体");
            string paper = GetString(a, "paper", "A3");
            bool clear = GetBool(a, "clear", true);
            var frameArg = a["frame"] as JsonObject;
            if (frameArg == null)
                throw new InvalidOperationException(
                    "缺少 frame{block,from_dwg,x,y,scale,attributes}。");

            double paperW, paperH;
            PaperSizeMm(paper, out paperW, out paperH);

            Database db = doc.Database;
            var lm = LayoutManager.Current;
            ObjectId layoutId = lm.GetLayoutId(layoutName);
            bool created = layoutId.IsNull;
            if (created) layoutId = lm.CreateLayout(layoutName);
            if (!string.Equals(lm.CurrentLayout, layoutName, StringComparison.Ordinal))
                lm.CurrentLayout = layoutName;

            var sourceArgs = a["sources"] as JsonArray;
            if (sourceArgs == null || sourceArgs.Count == 0)
            {
                sourceArgs = new JsonArray
                {
                    new JsonObject
                    {
                        ["target"] = new JsonObject
                        {
                            ["x"] = 20, ["y"] = 72,
                            ["width"] = paperW - 35, ["height"] = paperH - 92
                        }
                    }
                };
            }

            var copied = new JsonArray();
            var drawn = new JsonArray();
            int totalCloned = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForWrite);
                var paperSpace = (BlockTableRecord)tr.GetObject(
                    layout.BlockTableRecordId, OpenMode.ForWrite);

                if (clear)
                {
                    foreach (ObjectId id in paperSpace)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        var oldVp = ent as Autodesk.AutoCAD.DatabaseServices.Viewport;
                        if (oldVp != null && oldVp.Number == 1) continue;
                        ent.UpgradeOpen();
                        ent.Erase();
                    }
                }

                int sourceIndex = 0;
                foreach (JsonNode node in sourceArgs)
                {
                    var srcArg = node as JsonObject;
                    if (srcArg == null) continue;
                    sourceIndex++;

                    var target = srcArg["target"] as JsonObject;
                    double tx = target == null ? 20 : GetDouble(target, "x", 20);
                    double ty = target == null ? 72 : GetDouble(target, "y", 72);
                    double tw = target == null ? paperW - 35 : GetDouble(target, "width", paperW - 35);
                    double th = target == null ? paperH - 92 : GetDouble(target, "height", paperH - 92);
                    if (tw <= 0 || th <= 0)
                        throw new InvalidOperationException("source target 的 width/height 必须大于 0。");
                    double rotation = GetDouble(srcArg, "rotation", 0) * Math.PI / 180.0;
                    string sourcePath = GetString(srcArg, "dwg", null);
                    var sourceWindow = srcArg["source_window"] as JsonObject;
                    bool crossing = GetBool(srcArg, "crossing", false);
                    double swMinX = sourceWindow == null ? double.NegativeInfinity : GetDouble(sourceWindow, "minx", 0);
                    double swMinY = sourceWindow == null ? double.NegativeInfinity : GetDouble(sourceWindow, "miny", 0);
                    double swMaxX = sourceWindow == null ? double.PositiveInfinity : GetDouble(sourceWindow, "maxx", 0);
                    double swMaxY = sourceWindow == null ? double.PositiveInfinity : GetDouble(sourceWindow, "maxy", 0);
                    if (sourceWindow != null && (swMaxX <= swMinX || swMaxY <= swMinY))
                        throw new InvalidOperationException("source_window 范围无效。");
                    var excludedLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var excludedArg = srcArg["exclude_layers"] as JsonArray;
                    if (excludedArg != null)
                        foreach (JsonNode layerNode in excludedArg)
                            if (layerNode != null) excludedLayers.Add(layerNode.ToString());

                    var ids = new ObjectIdCollection();
                    Extents3d bounds = new Extents3d();
                    bool haveBounds = false;
                    var map = new IdMapping();

                    if (string.IsNullOrEmpty(sourcePath))
                    {
                        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        var ms = (BlockTableRecord)tr.GetObject(
                            bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                        foreach (ObjectId id in ms)
                        {
                            var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent == null || ent.IsErased) continue;
                            if (excludedLayers.Contains(ent.Layer)) continue;
                            try
                            {
                                Extents3d ex = ent.GeometricExtents;
                                if (sourceWindow != null)
                                {
                                    bool inside = ex.MinPoint.X >= swMinX && ex.MaxPoint.X <= swMaxX &&
                                                  ex.MinPoint.Y >= swMinY && ex.MaxPoint.Y <= swMaxY;
                                    bool intersects = !(ex.MaxPoint.X < swMinX || ex.MinPoint.X > swMaxX ||
                                                        ex.MaxPoint.Y < swMinY || ex.MinPoint.Y > swMaxY);
                                    if (crossing ? !intersects : !inside) continue;
                                }
                                if (!haveBounds) { bounds = ex; haveBounds = true; }
                                else bounds.AddExtents(ex);
                                ids.Add(id);
                            }
                            catch { }
                        }
                        if (ids.Count == 0 || !haveBounds)
                            throw new InvalidOperationException("当前图模型空间没有可复制实体。");
                        db.DeepCloneObjects(ids, paperSpace.ObjectId, map, false);
                        sourcePath = SafeFile(db);
                    }
                    else
                    {
                        if (!File.Exists(sourcePath))
                            throw new InvalidOperationException("找不到材料源 DWG：" + sourcePath);
                        using (var src = new Database(false, true))
                        {
                            src.ReadDwgFile(sourcePath, FileOpenMode.OpenForReadAndAllShare, true, null);
                            src.CloseInput(true);
                            using (var stx = src.TransactionManager.StartTransaction())
                            {
                                var sbt = (BlockTable)stx.GetObject(src.BlockTableId, OpenMode.ForRead);
                                var sms = (BlockTableRecord)stx.GetObject(
                                    sbt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                                foreach (ObjectId id in sms)
                                {
                                    var ent = stx.GetObject(id, OpenMode.ForRead) as Entity;
                                    if (ent == null || ent.IsErased) continue;
                                    if (excludedLayers.Contains(ent.Layer)) continue;
                                    try
                                    {
                                        Extents3d ex = ent.GeometricExtents;
                                        if (sourceWindow != null)
                                        {
                                            bool inside = ex.MinPoint.X >= swMinX && ex.MaxPoint.X <= swMaxX &&
                                                          ex.MinPoint.Y >= swMinY && ex.MaxPoint.Y <= swMaxY;
                                            bool intersects = !(ex.MaxPoint.X < swMinX || ex.MinPoint.X > swMaxX ||
                                                                ex.MaxPoint.Y < swMinY || ex.MinPoint.Y > swMaxY);
                                            if (crossing ? !intersects : !inside) continue;
                                        }
                                        if (!haveBounds) { bounds = ex; haveBounds = true; }
                                        else bounds.AddExtents(ex);
                                        ids.Add(id);
                                    }
                                    catch { }
                                }
                                if (ids.Count == 0 || !haveBounds)
                                    throw new InvalidOperationException(
                                        "材料源模型空间没有可复制实体：" + sourcePath);
                                src.WblockCloneObjects(ids, paperSpace.ObjectId, map,
                                                       DuplicateRecordCloning.Ignore, false);
                                stx.Commit();
                            }
                        }
                    }

                    double sourceW = bounds.MaxPoint.X - bounds.MinPoint.X;
                    double sourceH = bounds.MaxPoint.Y - bounds.MinPoint.Y;
                    if (sourceW <= 0 || sourceH <= 0)
                        throw new InvalidOperationException("材料源范围无效：" + sourcePath);
                    bool fixedScale = srcArg["paper_scale"] != null;
                    double scale;
                    Point3d sourceOrigin;
                    Point3d targetOrigin;
                    if (fixedScale)
                    {
                        scale = GetDouble(srcArg, "paper_scale", 0);
                        if (scale <= 0)
                            throw new InvalidOperationException("paper_scale 必须大于 0。");
                        sourceOrigin = sourceWindow == null
                            ? bounds.MinPoint
                            : new Point3d(swMinX, swMinY, 0);
                        targetOrigin = new Point3d(tx, ty, 0);
                    }
                    else
                    {
                        double fitW = Math.Abs(Math.Cos(rotation)) * sourceW +
                                      Math.Abs(Math.Sin(rotation)) * sourceH;
                        double fitH = Math.Abs(Math.Sin(rotation)) * sourceW +
                                      Math.Abs(Math.Cos(rotation)) * sourceH;
                        scale = Math.Min(tw / fitW, th / fitH);
                        scale *= GetDouble(srcArg, "fit_factor", 0.98);
                        sourceOrigin = new Point3d(
                            (bounds.MinPoint.X + bounds.MaxPoint.X) / 2.0,
                            (bounds.MinPoint.Y + bounds.MaxPoint.Y) / 2.0, 0);
                        targetOrigin = new Point3d(tx + tw / 2.0, ty + th / 2.0, 0);
                    }
                    int clonedHere = 0;
                    foreach (IdPair pair in map)
                    {
                        if (!pair.IsCloned || pair.Value.IsNull) continue;
                        var ent = tr.GetObject(pair.Value, OpenMode.ForWrite, false) as Entity;
                        if (ent == null || ent.IsErased) continue;
                        ent.TransformBy(Matrix3d.Displacement(Point3d.Origin - sourceOrigin));
                        ent.TransformBy(Matrix3d.Scaling(scale, Point3d.Origin));
                        if (Math.Abs(rotation) > 1e-12)
                            ent.TransformBy(Matrix3d.Rotation(
                                rotation, Vector3d.ZAxis, Point3d.Origin));
                        ent.TransformBy(Matrix3d.Displacement(targetOrigin - Point3d.Origin));
                        clonedHere++;
                    }
                    totalCloned += clonedHere;
                    copied.Add(new JsonObject
                    {
                        ["source"] = sourcePath,
                        ["entities"] = clonedHere,
                        ["source_width"] = Math.Round(sourceW, 4),
                        ["source_height"] = Math.Round(sourceH, 4),
                        ["paper_scale"] = Math.Round(scale, 8),
                        ["scale_mode"] = fixedScale ? "fixed" : "fit",
                        ["target"] = new JsonObject
                        {
                            ["x"] = tx, ["y"] = ty, ["width"] = tw, ["height"] = th
                        }
                    });
                }

                var notes = a["notes"] as JsonArray;
                if (notes != null && notes.Count > 0)
                {
                    var noteText = new System.Text.StringBuilder();
                    int n = 0;
                    foreach (JsonNode note in notes)
                    {
                        if (n > 0) noteText.Append("\\P");
                        noteText.Append((n + 1).ToString(CultureInfo.InvariantCulture));
                        noteText.Append("、");
                        noteText.Append(note == null ? "" : note.ToString());
                        n++;
                    }
                    var mt = new MText
                    {
                        Location = new Point3d(
                            GetDouble(a, "notes_x", 30),
                            GetDouble(a, "notes_y", 58), 0),
                        Width = GetDouble(a, "notes_width", 230),
                        TextHeight = GetDouble(a, "notes_height", 2.5),
                        Attachment = AttachmentPoint.TopLeft,
                        Contents = "说明：" + "\\P" + noteText.ToString()
                    };
                    paperSpace.AppendEntity(mt);
                    tr.AddNewlyCreatedDBObject(mt, true);
                    drawn.Add(new JsonObject { ["type"] = "MText", ["space"] = layoutName });
                }

                // 图框文件本身位于模型空间；不给 source_block 时整张 DWG 作为块定义导入。
                var frame = (JsonObject)JsonNode.Parse(frameArg.ToJsonString());
                string frameName = Need(frame, "block");
                string frameDwg = GetString(frame, "from_dwg", null);
                string frameSourceBlock = GetString(frame, "source_block", null);
                var bt2 = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                // Database.Insert 会在含大量 AEC 依赖的宿主图中长时间高 CPU。
                // 对“整张 DWG 当图框”的场景，直接把源模型空间实体克隆进新块定义。
                if (!bt2.Has(frameName) && !string.IsNullOrEmpty(frameDwg) &&
                    string.IsNullOrEmpty(frameSourceBlock))
                {
                    if (!File.Exists(frameDwg))
                        throw new InvalidOperationException("找不到图框源文件：" + frameDwg);
                    bt2.UpgradeOpen();
                    var frameDef = new BlockTableRecord
                    {
                        Name = frameName,
                        Origin = Point3d.Origin
                    };
                    ObjectId frameDefId = bt2.Add(frameDef);
                    tr.AddNewlyCreatedDBObject(frameDef, true);

                    using (var frameDb = new Database(false, true))
                    {
                        frameDb.ReadDwgFile(
                            frameDwg, FileOpenMode.OpenForReadAndAllShare, true, null);
                        frameDb.CloseInput(true);
                        using (var ftx = frameDb.TransactionManager.StartTransaction())
                        {
                            var fbt = (BlockTable)ftx.GetObject(
                                frameDb.BlockTableId, OpenMode.ForRead);
                            var fms = (BlockTableRecord)ftx.GetObject(
                                fbt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                            var frameIds = new ObjectIdCollection();
                            foreach (ObjectId id in fms) frameIds.Add(id);
                            if (frameIds.Count == 0)
                                throw new InvalidOperationException(
                                    "图框源模型空间为空：" + frameDwg);
                            var frameMap = new IdMapping();
                            frameDb.WblockCloneObjects(
                                frameIds, frameDefId, frameMap,
                                DuplicateRecordCloning.Ignore, false);
                            ftx.Commit();
                        }
                    }
                    frame.Remove("from_dwg");
                }
                // compose_layout_sheet 对外使用 frame.block；复用通用插块逻辑时映射成 blocks[].name。
                frame["name"] = frameName;
                frame["space"] = layoutName;
                if (frame["x"] == null) frame["x"] = 0;
                if (frame["y"] == null) frame["y"] = 0;
                if (frame["scale"] == null) frame["scale"] = 1;
                var blockArgs = new JsonObject
                {
                    ["space"] = layoutName,
                    ["blocks"] = new JsonArray(frame)
                };
                var modelSpace = (BlockTableRecord)tr.GetObject(
                    bt2[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                InsertBlocks(db, tr, modelSpace, blockArgs, "0", drawn);

                PlotSettingsValidator psv = PlotSettingsValidator.Current;
                psv.SetPlotConfigurationName(layout, "DWG To PDF.pc3", null);
                psv.RefreshLists(layout);
                string media = null, firstMatch = null;
                foreach (string m in psv.GetCanonicalMediaNameList(layout))
                {
                    if (m.IndexOf(paper, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (firstMatch == null) firstMatch = m;
                    if (m.IndexOf("full_bleed", StringComparison.OrdinalIgnoreCase) >= 0)
                    { media = m; break; }
                }
                media = media ?? firstMatch;
                if (media == null)
                    throw new InvalidOperationException("DWG To PDF.pc3 没有纸张：" + paper);
                psv.SetCanonicalMediaName(layout, media);
                psv.SetPlotType(layout, AcDbPlotType.Layout);
                psv.SetUseStandardScale(layout, true);
                psv.SetStdScaleType(layout, StdScaleType.StdScale1To1);
                psv.SetPlotPaperUnits(layout, PlotPaperUnit.Millimeters);
                psv.SetPlotRotation(layout, PlotRotation.Degrees090);
                layout.PlotPlotStyles = true;
                psv.SetCurrentStyleSheet(layout, "monochrome.ctb");
                layout.PrintLineweights = true;
                tr.Commit();
            }

            try { lm.CurrentLayout = layoutName; } catch { }
            return new JsonObject
            {
                ["layout"] = layoutName,
                ["created"] = created,
                ["paper"] = paper + " " + paperW + "×" + paperH + " mm",
                ["mode"] = "实体直接复制到布局空间（无模型视口）",
                ["entities_cloned"] = totalCloned,
                ["sources"] = copied,
                ["items_added"] = drawn
            };
        }

        // ---------- 布局图纸：纸空间图框 + 模型空间视口 ----------
        static JsonNode CreateLayoutSheet(JsonObject a, Document doc)
        {
            string layoutName = GetString(a, "layout", "C3DF-A3");
            string blockName = Need(a, "block");
            string fromDwg = GetString(a, "from_dwg", null);
            string paper = GetString(a, "paper", "A3");
            var win = a["model_window"] as JsonObject;
            string boundaryHandle = GetString(a, "boundary_handle", null);
            string boundaryLayer = GetString(a, "boundary_layer", null);
            double minx = 0, miny = 0, maxx = 0, maxy = 0;
            string windowSource = "model_window";
            string matchedBoundaryHandle = null;

            if (win != null)
            {
                minx = GetDouble(win, "minx", 0); miny = GetDouble(win, "miny", 0);
                maxx = GetDouble(win, "maxx", 0); maxy = GetDouble(win, "maxy", 0);
            }
            else
            {
                if (string.IsNullOrEmpty(boundaryHandle) && string.IsNullOrEmpty(boundaryLayer))
                    throw new InvalidOperationException(
                        "需提供 model_window，或用 boundary_handle / boundary_layer 指定模型空间取材框。");
                Database lookupDb = doc.Database;
                using (Transaction lookupTr = lookupDb.TransactionManager.StartTransaction())
                {
                    var lookupBt = (BlockTable)lookupTr.GetObject(lookupDb.BlockTableId, OpenMode.ForRead);
                    var lookupMs = (BlockTableRecord)lookupTr.GetObject(
                        lookupBt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    Entity boundary = null;
                    foreach (ObjectId id in lookupMs)
                    {
                        var ent = lookupTr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        if (!string.IsNullOrEmpty(boundaryHandle) &&
                            !ent.Handle.ToString().Equals(boundaryHandle, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!string.IsNullOrEmpty(boundaryLayer) &&
                            ent.Layer.IndexOf(boundaryLayer, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        if (!(ent is Polyline) && !(ent is Polyline2d) && !(ent is Polyline3d) &&
                            !(ent is Circle))
                            continue;
                        boundary = ent;
                    }
                    if (boundary == null)
                        throw new InvalidOperationException("没找到指定的平面取材框。");
                    Extents3d ext = boundary.GeometricExtents;
                    minx = ext.MinPoint.X; miny = ext.MinPoint.Y;
                    maxx = ext.MaxPoint.X; maxy = ext.MaxPoint.Y;
                    matchedBoundaryHandle = boundary.Handle.ToString();
                    windowSource = !string.IsNullOrEmpty(boundaryHandle)
                        ? "boundary_handle" : "boundary_layer";
                    lookupTr.Commit();
                }
            }
            if (maxx <= minx || maxy <= miny)
                throw new InvalidOperationException("模型空间取材范围无效。");

            double paperW, paperH;
            PaperSizeMm(paper, out paperW, out paperH);

            var vpArg = a["viewport"] as JsonObject;
            double vpX = vpArg == null ? paperW * 0.42 : GetDouble(vpArg, "x", paperW * 0.42);
            double vpY = vpArg == null ? paperH * 0.52 : GetDouble(vpArg, "y", paperH * 0.52);
            double vpW = vpArg == null ? paperW - 70 : GetDouble(vpArg, "width", paperW - 70);
            double vpH = vpArg == null ? paperH - 30 : GetDouble(vpArg, "height", paperH - 30);
            double vpScale = vpArg == null ? 0 : GetDouble(vpArg, "scale", 0);
            bool vpLocked = vpArg == null ? true : GetBool(vpArg, "locked", true);
            if (vpW <= 0 || vpH <= 0) throw new InvalidOperationException("viewport 尺寸必须大于 0。");
            if (vpScale < 0) throw new InvalidOperationException("viewport.scale 必须大于 0。");

            double frameX = GetDouble(a, "frame_x", -9.372);
            double frameY = GetDouble(a, "frame_y", -10.01);
            double frameScale = GetDouble(a, "frame_scale", 1);
            string frameLayer = GetString(a, "frame_layer", "0");
            bool clearLayout = GetBool(a, "clear", true);

            Database db = doc.Database;
            var lm = LayoutManager.Current;
            ObjectId layoutId = lm.GetLayoutId(layoutName);
            bool created = layoutId.IsNull;
            if (created) layoutId = lm.CreateLayout(layoutName);
            // Viewport.On 只允许在当前纸空间布局中设置；否则抛 eNotInPaperspace。
            if (!string.Equals(lm.CurrentLayout, layoutName, StringComparison.Ordinal))
                lm.CurrentLayout = layoutName;

            int attributesFilled = 0;
            var extraVpReport = new JsonArray();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForWrite);
                var paperSpace = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);

                // 重跑时清空本布局中的普通实体和用户视口；保留系统的 1 号纸空间视口。
                // clear:false 则叠加——同一布局里横排多张图框+视口（合并分幅图用）。
                if (clearLayout)
                {
                    foreach (ObjectId id in paperSpace)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        var oldVp = ent as Autodesk.AutoCAD.DatabaseServices.Viewport;
                        if (oldVp != null && oldVp.Number == 1) continue;
                        ent.UpgradeOpen();
                        ent.Erase();
                    }
                }

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                if (!bt.Has(blockName))
                {
                    if (string.IsNullOrEmpty(fromDwg))
                        throw new InvalidOperationException("图中没有块 '" + blockName + "'，需提供 from_dwg。");
                    if (!File.Exists(fromDwg)) throw new InvalidOperationException("找不到块库文件：" + fromDwg);
                    using (var src = new Database(false, true))
                    {
                        src.ReadDwgFile(fromDwg, FileOpenMode.OpenForReadAndAllShare, true, null);
                        src.CloseInput(true);
                        using (var stx = src.TransactionManager.StartTransaction())
                        {
                            var sbt = (BlockTable)stx.GetObject(src.BlockTableId, OpenMode.ForRead);
                            if (sbt.Has(blockName))
                            {
                                var ids = new ObjectIdCollection { sbt[blockName] };
                                var map = new IdMapping();
                                db.WblockCloneObjects(ids, db.BlockTableId, map,
                                                      DuplicateRecordCloning.Replace, false);
                            }
                            else
                            {
                                // 图框库也可能是一张“裸图框 DWG”，没有同名块定义；
                                // 此时把整张 DWG 作为 blockName 导入。
                                db.Insert(blockName, src, false);
                            }
                            stx.Commit();
                        }
                    }
                    bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                }

                // 图框留在布局空间；本项目 TK 块的定义基点比外框左下角偏约 9.372/10.01 mm。
                var frame = new BlockReference(new Point3d(frameX, frameY, 0), bt[blockName])
                {
                    ScaleFactors = new Scale3d(frameScale),
                    Layer = frameLayer
                };
                paperSpace.AppendEntity(frame);
                tr.AddNewlyCreatedDBObject(frame, true);

                var wanted = a["attributes"] as JsonObject;
                var widthFactors = a["width_factors"] as JsonObject;   // {标签:因子}，长图名塞窄格用
                var frameDef = (BlockTableRecord)tr.GetObject(bt[blockName], OpenMode.ForRead);
                if (frameDef.HasAttributeDefinitions)
                {
                    foreach (ObjectId id in frameDef)
                    {
                        var ad = tr.GetObject(id, OpenMode.ForRead) as AttributeDefinition;
                        if (ad == null || ad.Constant) continue;
                        var ar = new AttributeReference();
                        ar.SetAttributeFromBlock(ad, frame.BlockTransform);
                        if (wanted != null && wanted[ad.Tag] != null)
                        {
                            ar.TextString = wanted[ad.Tag].ToString();
                            attributesFilled++;
                        }
                        if (widthFactors != null && widthFactors[ad.Tag] != null)
                        {
                            double wfv = GetDouble(widthFactors, ad.Tag, 0);
                            if (wfv > 0) ar.WidthFactor = wfv;
                        }
                        frame.AttributeCollection.AppendAttribute(ar);
                        tr.AddNewlyCreatedDBObject(ar, true);
                    }
                }

                // 视口边界缺省放不打印图层；viewport.layer 可指定别的层（要打印边框就给个可打印层）。
                const string vpLayerName = "C3DF-VPORT-NOPLOT";
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!lt.Has(vpLayerName))
                {
                    lt.UpgradeOpen();
                    var ltr = new LayerTableRecord { Name = vpLayerName, IsPlottable = false };
                    lt.Add(ltr);
                    tr.AddNewlyCreatedDBObject(ltr, true);
                    lt.DowngradeOpen();
                }

                // 源图图层常带着「新视口中冻结」状态，新建视口会把这些层整层隐身
                // （DXF 解析读不出来，项目B SY5 现状高程整层消失就是它）。
                // 所以每个新视口先全层解冻、再按参数冻结——行为与来源图状态无关。
                Func<string, ObjectId> layerIdStrict = name =>
                {
                    if (!lt.Has(name))
                        throw new InvalidOperationException("视口图层参数：图里没有图层 '" + name + "'。");
                    return lt[name];
                };
                Action<Autodesk.AutoCAD.DatabaseServices.Viewport, JsonArray, string> applyVpLayerState =
                    (vport, freezeArr, tag) =>
                {
                    var all = new List<ObjectId>();
                    foreach (ObjectId lid in lt) all.Add(lid);
                    vport.ThawLayersInViewport(all.GetEnumerator());
                    if (freezeArr != null && freezeArr.Count > 0)
                    {
                        var freezeIds = new List<ObjectId>();
                        foreach (JsonNode fn in freezeArr)
                        {
                            string ln = fn?.ToString();
                            if (string.IsNullOrEmpty(ln)) continue;
                            if (!lt.Has(ln))
                                throw new InvalidOperationException(
                                    tag + ".freeze_layers：图里没有图层 '" + ln + "'。");
                            freezeIds.Add(lt[ln]);
                        }
                        if (freezeIds.Count > 0) vport.FreezeLayersInViewport(freezeIds.GetEnumerator());
                    }
                };

                string vpLayer = vpArg == null ? vpLayerName : GetString(vpArg, "layer", vpLayerName);
                if (vpLayer != vpLayerName) layerIdStrict(vpLayer);
                double modelW = maxx - minx, modelH = maxy - miny;
                double fitViewHeight = Math.Max(modelH, modelW * vpH / vpW) * 1.04;
                var vp = new Autodesk.AutoCAD.DatabaseServices.Viewport
                {
                    CenterPoint = new Point3d(vpX, vpY, 0),
                    Width = vpW,
                    Height = vpH,
                    ViewTarget = new Point3d((minx + maxx) / 2.0, (miny + maxy) / 2.0, 0),
                    ViewCenter = Point2d.Origin,
                    ViewHeight = fitViewHeight,
                    TwistAngle = 0,
                    Layer = vpLayer
                };
                paperSpace.AppendEntity(vp);
                tr.AddNewlyCreatedDBObject(vp, true);
                vp.On = true;
                if (vpScale > 0) vp.CustomScale = 1000.0 / vpScale;
                applyVpLayerState(vp, vpArg == null ? null : vpArg["freeze_layers"] as JsonArray, "viewport");
                vp.Locked = vpLocked;

                // 附加视口：同一图框里再开小窗（如分幅图左下角的"平面索引"钥匙图）。
                // 每项自带纸位/尺寸/比例/模型中心；缺中心就沿用主窗中心。
                var extraVps = a["extra_viewports"] as JsonArray;
                if (extraVps != null)
                {
                    foreach (JsonNode en in extraVps)
                    {
                        var ev = en as JsonObject;
                        if (ev == null) continue;
                        double exW = GetDouble(ev, "width", 0), exH = GetDouble(ev, "height", 0);
                        double exScale = GetDouble(ev, "scale", 0);
                        if (exW <= 0 || exH <= 0 || exScale <= 0)
                            throw new InvalidOperationException(
                                "extra_viewports 每项必须给 width / height / scale（均 > 0）。");
                        double exX = GetDouble(ev, "x", 0), exY = GetDouble(ev, "y", 0);
                        double exCx = GetDouble(ev, "center_x", (minx + maxx) / 2.0);
                        double exCy = GetDouble(ev, "center_y", (miny + maxy) / 2.0);
                        string exLayer = GetString(ev, "layer", vpLayerName);
                        if (exLayer != vpLayerName) layerIdStrict(exLayer);
                        var evp = new Autodesk.AutoCAD.DatabaseServices.Viewport
                        {
                            CenterPoint = new Point3d(exX, exY, 0),
                            Width = exW,
                            Height = exH,
                            ViewTarget = new Point3d(exCx, exCy, 0),
                            ViewCenter = Point2d.Origin,
                            ViewHeight = exH * exScale / 1000.0,
                            TwistAngle = 0,
                            Layer = exLayer
                        };
                        paperSpace.AppendEntity(evp);
                        tr.AddNewlyCreatedDBObject(evp, true);
                        evp.On = true;
                        evp.CustomScale = 1000.0 / exScale;
                        // 先全层解冻（洗掉「新视口中冻结」带进来的隐身），再冻结点名的层——铁律 13 名字必须全中
                        applyVpLayerState(evp, ev["freeze_layers"] as JsonArray, "extra_viewports");
                        evp.Locked = GetBool(ev, "locked", true);
                        extraVpReport.Add(new JsonObject
                        {
                            ["center_x"] = exX, ["center_y"] = exY,
                            ["width"] = exW, ["height"] = exH,
                            ["scale"] = exScale,
                            ["model_center_x"] = exCx, ["model_center_y"] = exCy
                        });
                    }
                }

                // 把布局本身配置成 A3 横向 1:1；打印时可直接指定该布局。
                PlotSettingsValidator psv = PlotSettingsValidator.Current;
                psv.SetPlotConfigurationName(layout, "DWG To PDF.pc3", null);
                psv.RefreshLists(layout);
                string media = null, firstMatch = null;
                foreach (string m in psv.GetCanonicalMediaNameList(layout))
                {
                    if (m.IndexOf(paper, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (firstMatch == null) firstMatch = m;
                    if (m.IndexOf("full_bleed", StringComparison.OrdinalIgnoreCase) >= 0)
                    { media = m; break; }
                }
                media = media ?? firstMatch;
                if (media == null) throw new InvalidOperationException("DWG To PDF.pc3 没有纸张：" + paper);
                psv.SetCanonicalMediaName(layout, media);
                psv.SetPlotType(layout, AcDbPlotType.Layout);
                psv.SetUseStandardScale(layout, true);
                psv.SetStdScaleType(layout, StdScaleType.StdScale1To1);
                psv.SetPlotPaperUnits(layout, PlotPaperUnit.Millimeters);
                psv.SetPlotRotation(layout, PlotRotation.Degrees090);
                layout.PlotPlotStyles = true;
                psv.SetCurrentStyleSheet(layout, "monochrome.ctb");
                layout.PrintLineweights = true;

                tr.Commit();
            }
            try { lm.CurrentLayout = layoutName; } catch { }

            return new JsonObject
            {
                ["layout"] = layoutName,
                ["created"] = created,
                ["paper"] = paper + " " + paperW + "×" + paperH + " mm",
                ["frame_space"] = "Layout",
                ["frame_block"] = blockName,
                ["attributes_filled"] = attributesFilled,
                ["viewport"] = new JsonObject
                {
                    ["center_x"] = vpX, ["center_y"] = vpY,
                    ["width"] = vpW, ["height"] = vpH,
                    ["scale"] = vpScale > 0 ? JsonValue.Create(vpScale) : null,
                    ["locked"] = vpLocked
                },
                ["extra_viewports"] = extraVpReport,
                ["model_window"] = new JsonObject
                {
                    ["minx"] = minx, ["miny"] = miny, ["maxx"] = maxx, ["maxy"] = maxy,
                    ["source"] = windowSource,
                    ["boundary_handle"] = matchedBoundaryHandle
                }
            };
        }

        // ---------- 打印 PDF ----------
        // 从已实战验证的 CadPlotPlugin 移植（2026-07-23 出 81 张 PDF 零错误），
        // 三个坑一并带过来：①侧库设为 WorkingDatabase ②目标布局须为当前布局
        // ③侧库上 MediaMatchingPolicy 必须 MatchEnabled。
        static JsonNode PlotPdf(JsonObject a, Document doc)
        {
            string outPdf = GetString(a, "out", null);
            if (string.IsNullOrEmpty(outPdf))
                throw new InvalidOperationException("缺少参数 out（PDF 输出路径）。");
            if (!Path.IsPathRooted(outPdf))
                throw new InvalidOperationException("out 必须是绝对路径：" + outPdf);
            if (File.Exists(outPdf) && !GetBool(a, "overwrite", false))
                throw new InvalidOperationException(
                    "PDF 已存在，拒绝覆盖：" + outPdf + "（确需覆盖请传 overwrite:true）");

            string outDir = Path.GetDirectoryName(outPdf);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            string extDwg = GetString(a, "dwg", null);
            string layoutName = GetString(a, "layout", "Model");
            string paper = GetString(a, "paper", "A3");
            string ctb = GetString(a, "ctb", "monochrome.ctb");
            bool lineweights = GetBool(a, "lineweights", true);
            bool fitLayout = GetBool(a, "fit", false);
            string device = GetString(a, "device", "DWG To PDF.pc3");   // any installed plotter, e.g. "PublishToWeb PNG.pc3" for a PNG screenshot
            long minBytes = (long)GetDouble(a, "min_bytes", 1024);

            Database prevWorking = HostApplicationServices.WorkingDatabase;
            Database side = null;
            try
            {
                Database db;
                if (!string.IsNullOrEmpty(extDwg))
                {
                    if (!File.Exists(extDwg))
                        throw new InvalidOperationException("找不到要打印的 dwg：" + extDwg);
                    side = new Database(false, true);
                    side.ReadDwgFile(extDwg, FileOpenMode.OpenForReadAndAllShare, true, null);
                    side.CloseInput(true);
                    // 坑①：侧数据库必须设为当前工作库，PlotEngine 才能出它的布局
                    HostApplicationServices.WorkingDatabase = side;
                    db = side;
                }
                else db = doc.Database;

                // 模型空间的打印窗口是按**当前 UCS** 解释的，不是世界坐标系。
                // 图里 UCS 有偏移时，传世界坐标的窗口就会打到别处去（现象：内容不对或白纸，不报错）。
                // 打印前把 UCS 归零，窗口参数才等于世界坐标。
                try { doc.Editor.CurrentUserCoordinateSystem = Matrix3d.Identity; } catch { }

                ObjectId layoutId;
                Extents3d window;
                bool userWindow;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var lm = LayoutManager.Current;
                    layoutId = lm.GetLayoutId(layoutName);
                    if (layoutId.IsNull)
                        throw new InvalidOperationException("找不到布局 '" + layoutName + "'。");
                    // 坑②：PlotInfoValidator 要求目标布局必须是当前布局，否则 eLayoutNotCurrent
                    try
                    {
                        if (!string.Equals(lm.CurrentLayout, layoutName, StringComparison.Ordinal))
                            lm.CurrentLayout = layoutName;
                    }
                    catch { }

                    var win = a["window"] as JsonObject;
                    userWindow = win != null;
                    if (win != null)
                    {
                        window = new Extents3d(
                            new Point3d(GetDouble(win, "minx", 0), GetDouble(win, "miny", 0), 0),
                            new Point3d(GetDouble(win, "maxx", 0), GetDouble(win, "maxy", 0), 0));
                    }
                    else
                    {
                        // 缺省用图纸范围；空图或范围未初始化时 Extmin>Extmax，直接报错更清楚
                        db.UpdateExt(true);
                        if (db.Extmin.X > db.Extmax.X)
                            throw new InvalidOperationException("图纸范围为空，无法自动确定打印窗口——请传 window 参数。");
                        window = new Extents3d(db.Extmin, db.Extmax);
                    }
                    tr.Commit();
                }

                Extents3d plotWindow = window;
                if (side == null && string.Equals(layoutName, "Model",
                    StringComparison.OrdinalIgnoreCase))
                {
                    // SetPlotWindowArea 接受的是当前视图 DCS，不是图形 WCS。
                    // 远离原点的模型空间图框若直接传 WCS，会正常生成 PDF 但内容是白纸。
                    using (ViewTableRecord view = doc.Editor.GetCurrentView())
                    {
                        Matrix3d wcsToDcs = Matrix3d.PlaneToWorld(view.ViewDirection);
                        wcsToDcs = Matrix3d.Displacement(
                            view.Target - Point3d.Origin) * wcsToDcs;
                        wcsToDcs = Matrix3d.Rotation(
                            -view.ViewTwist, view.ViewDirection, view.Target) * wcsToDcs;
                        wcsToDcs = wcsToDcs.Inverse();
                        plotWindow = TransformWindowExtents(window, wcsToDcs);
                    }
                }

                PlotWindow(db, layoutId, plotWindow, outPdf, paper, ctb, lineweights, fitLayout, userWindow, device);

                long size = File.Exists(outPdf) ? new FileInfo(outPdf).Length : 0;
                if (size < minBytes)
                    throw new InvalidOperationException("Plot output missing or too small (" + size + " bytes): " + outPdf);

                return new JsonObject
                {
                    ["pdf"] = outPdf,
                    ["bytes"] = size,
                    ["source"] = string.IsNullOrEmpty(extDwg) ? SafeFile(doc.Database) : extDwg,
                    ["layout"] = layoutName,
                    ["paper"] = paper,
                    ["ctb"] = string.IsNullOrEmpty(ctb) ? "(彩色)" : ctb,
                    ["window"] = new JsonObject
                    {
                        ["minx"] = Math.Round(window.MinPoint.X, 4),
                        ["miny"] = Math.Round(window.MinPoint.Y, 4),
                        ["maxx"] = Math.Round(window.MaxPoint.X, 4),
                        ["maxy"] = Math.Round(window.MaxPoint.Y, 4)
                    },
                    ["plot_window_dcs"] = new JsonObject
                    {
                        ["minx"] = Math.Round(plotWindow.MinPoint.X, 4),
                        ["miny"] = Math.Round(plotWindow.MinPoint.Y, 4),
                        ["maxx"] = Math.Round(plotWindow.MaxPoint.X, 4),
                        ["maxy"] = Math.Round(plotWindow.MaxPoint.Y, 4)
                    }
                };
            }
            finally
            {
                HostApplicationServices.WorkingDatabase = prevWorking;
                if (side != null) side.Dispose();
            }
        }

        static Extents3d TransformWindowExtents(Extents3d source, Matrix3d transform)
        {
            var points = new[]
            {
                new Point3d(source.MinPoint.X, source.MinPoint.Y, 0),
                new Point3d(source.MaxPoint.X, source.MinPoint.Y, 0),
                new Point3d(source.MaxPoint.X, source.MaxPoint.Y, 0),
                new Point3d(source.MinPoint.X, source.MaxPoint.Y, 0)
            };
            var result = new Extents3d(
                points[0].TransformBy(transform), points[0].TransformBy(transform));
            for (int i = 1; i < points.Length; i++)
                result.AddPoint(points[i].TransformBy(transform));
            return result;
        }

        // 窗口出图核心（移植自 CadPlotPlugin.PlotWindow）
        static void PlotWindow(Database db, ObjectId layoutId, Extents3d window,
                               string outPdf, string paper, string ctb, bool printLineweights,
                               bool fitLayout = false, bool userWindow = false, string device = "DWG To PDF.pc3")
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);

                var ps = new PlotSettings(layout.ModelType);
                ps.CopyFrom(layout);
                PlotSettingsValidator psv = PlotSettingsValidator.Current;
                psv.SetPlotConfigurationName(ps, device, null);
                psv.RefreshLists(ps);
                // Raster drivers (PublishToWeb PNG/JPG) measure paper in pixels; mm is eInvalidInput for them.
                bool raster = device.IndexOf("PNG", StringComparison.OrdinalIgnoreCase) >= 0
                           || device.IndexOf("JPG", StringComparison.OrdinalIgnoreCase) >= 0;
                PlotPaperUnit units = raster ? PlotPaperUnit.Pixels : PlotPaperUnit.Millimeters;

                // 优先选 full_bleed 纸张（无边距，图框才不会被裁）
                string media = null, firstMatch = null;
                foreach (string m in psv.GetCanonicalMediaNameList(ps))
                {
                    if (m.IndexOf(paper, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (firstMatch == null) firstMatch = m;
                    if (m.IndexOf("full_bleed", StringComparison.OrdinalIgnoreCase) >= 0) { media = m; break; }
                }
                media = media ?? firstMatch;
                if (media == null)
                    throw new InvalidOperationException(device + " has no media matching '" + paper + "'.");
                psv.SetCanonicalMediaName(ps, media);

                if (layout.ModelType)
                {
                    // AutoCAD 2025 模型空间实测：必须先写窗口，再切 Window。
                    // 先 SetPlotType 会在部分模型布局状态下直接抛 eInvalidInput。
                    psv.SetPlotWindowArea(ps, new Extents2d(
                        window.MinPoint.X, window.MinPoint.Y, window.MaxPoint.X, window.MaxPoint.Y));
                    psv.SetPlotType(ps, AcDbPlotType.Window);
                    psv.SetUseStandardScale(ps, true);
                    psv.SetStdScaleType(ps, StdScaleType.ScaleToFit);
                    psv.SetPlotCentered(ps, true);
                    psv.SetPlotPaperUnits(ps, units);

                    // 横图转 90 度贴合竖纸
                    double w = window.MaxPoint.X - window.MinPoint.X;
                    double h = window.MaxPoint.Y - window.MinPoint.Y;
                    psv.SetPlotRotation(ps, (w >= h && !raster) ? PlotRotation.Degrees090 : PlotRotation.Degrees000);
                }
                else if (userWindow && !fitLayout)
                {
                    // 纸空间 + 显式窗口：窗口坐标就是布局图纸坐标（mm）。
                    // 顺序关键：先 SetPlotWindowArea 再 SetPlotType(Window)——反过来在部分
                    // 布局状态下抛 eInvalidInput（CadPlotPlugin.PlotWindow 同款打法，已验证；
                    // 旧注释"对布局调用 Window 必报 eInvalidInput"是先切类型后设窗口的结果）。
                    // 用途：从横排多图框的合并布局（如 分平面图-全）逐框抠出单张 A3。
                    psv.SetPlotWindowArea(ps, new Extents2d(
                        window.MinPoint.X, window.MinPoint.Y,
                        window.MaxPoint.X, window.MaxPoint.Y));
                    psv.SetPlotType(ps, AcDbPlotType.Window);
                    psv.SetUseStandardScale(ps, true);
                    psv.SetStdScaleType(ps, StdScaleType.ScaleToFit);
                    psv.SetPlotCentered(ps, true);
                    psv.SetPlotPaperUnits(ps, units);
                    double w = window.MaxPoint.X - window.MinPoint.X;
                    double h = window.MaxPoint.Y - window.MinPoint.Y;
                    psv.SetPlotRotation(ps, (w >= h && !raster) ? PlotRotation.Degrees090 : PlotRotation.Degrees000);
                }
                else
                {
                    // 纸空间布局缺省按 Layout 打印（窗口未显式给出时，window 只是图纸范围兜底值，
                    // 不能当打印窗口用）。
                    psv.SetPlotType(ps, fitLayout ? AcDbPlotType.Extents : AcDbPlotType.Layout);
                    psv.SetUseStandardScale(ps, true);
                    psv.SetStdScaleType(ps, fitLayout ? StdScaleType.ScaleToFit
                                                      : StdScaleType.StdScale1To1);
                    psv.SetPlotPaperUnits(ps, units);
                    if (fitLayout)
                    {
                        // 长条布局（多图框横排）转正贴纸：横宽竖高按纸张长边摆。
                        psv.SetPlotCentered(ps, true);
                        psv.SetPlotRotation(ps, PlotRotation.Degrees000);
                    }
                    else psv.SetPlotRotation(ps, PlotRotation.Degrees090);
                }

                if (!string.IsNullOrEmpty(ctb))
                {
                    ps.PlotPlotStyles = true;
                    psv.SetCurrentStyleSheet(ps, ctb);
                }
                else ps.PlotPlotStyles = false;

                ps.PrintLineweights = printLineweights;   // 治 hairline 细字淡显
                ps.ScaleLineweights = false;

                var pi = new PlotInfo { Layout = layoutId, OverrideSettings = ps };
                // 坑③：侧库上仍需开启介质匹配，关闭会抛 eNoMatchingMedia
                var piv = new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled };
                piv.Validate(pi);

                using (PlotEngine pe = PlotFactory.CreatePublishEngine())
                {
                    pe.BeginPlot(null, null);
                    pe.BeginDocument(pi, outPdf, null, 1, true, outPdf);
                    using (var ppi = new PlotPageInfo())
                    {
                        pe.BeginPage(ppi, pi, true, null);
                        pe.BeginGenerateGraphics(null);
                        pe.EndGenerateGraphics(null);
                        pe.EndPage(null);
                    }
                    pe.EndDocument(null);
                    pe.EndPlot(null);
                }
                tr.Commit();
            }
        }

        // 反射 dump：在 acc 进程内查对象真实 API（AeccDbMgd 是混合模式程序集，进程外无法反射）
        static JsonNode Snoop(JsonObject a, Document doc)
        {
            string typeFilter = GetString(a, "type", null);
            string nameFilter = GetString(a, "name", null);
            string handle = GetString(a, "handle", null);
            int max = (int)GetDouble(a, "max", 80);

            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                DBObject target = null;
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    DBObject o = tr.GetObject(id, OpenMode.ForRead);
                    string tn = o.GetType().Name;
                    if (!string.IsNullOrEmpty(handle))
                    {
                        if (o.Handle.ToString().Equals(handle, StringComparison.OrdinalIgnoreCase))
                        { target = o; break; }
                        continue;
                    }
                    if (!string.IsNullOrEmpty(typeFilter) &&
                        tn.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!string.IsNullOrEmpty(nameFilter))
                    {
                        string nm = TryGetName(o);
                        if (nm == null || nm != nameFilter) continue;
                    }
                    target = o; break;
                }
                if (target == null)
                    throw new InvalidOperationException("没找到匹配对象（type/name/handle 条件过严？）");

                Type t = target.GetType();
                var props = new JsonArray();
                int n = 0;
                foreach (var p in t.GetProperties(
                             System.Reflection.BindingFlags.Public |
                             System.Reflection.BindingFlags.Instance))
                {
                    if (n++ >= max) break;
                    var item = new JsonObject
                    {
                        ["name"] = p.Name,
                        ["type"] = p.PropertyType.Name
                    };
                    if (p.CanRead && p.GetIndexParameters().Length == 0)
                    {
                        try
                        {
                            object v = p.GetValue(target, null);
                            item["value"] = v == null ? "(null)" : Truncate(v.ToString(), 200);
                        }
                        catch (System.Exception ex)
                        {
                            item["value"] = "(取值失败: " + ex.GetType().Name + ")";
                        }
                    }
                    else item["value"] = "(不可读)";
                    props.Add(item);
                }
                tr.Commit();

                return new JsonObject
                {
                    ["object_type"] = t.FullName,
                    ["base_type"] = t.BaseType != null ? t.BaseType.FullName : "",
                    ["handle"] = target.Handle.ToString(),
                    ["property_count"] = props.Count,
                    ["properties"] = props
                };
            }
        }

        // 反射取 Name。注意：Civil 样式类在派生层用 new 重新声明了 Name，
        // GetProperty("Name") 会抛 AmbiguousMatchException（表现为"取不到名字、退化成类名"），
        // 所以改成遍历 GetProperties() 取第一个可读的 Name。
        static string TryGetName(object o)
        {
            if (o == null) return null;
            // Civil 样式类在派生层用 new 重新声明了 Name，且派生层那个**没有 get**：
            // GetProperty("Name") 会命中派生层的、CanRead 说 true、GetValue 却报
            // "Property Get method was not found."。所以逐层往基类走，找到第一个真有 getter 的。
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                      | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                try
                {
                    var p = t.GetProperty("Name", flags);
                    if (p == null) continue;
                    var getter = p.GetGetMethod(true);
                    if (getter == null) continue;
                    object v = getter.Invoke(o, null);
                    if (v != null) return v.ToString();
                }
                catch { }
            }
            return null;
        }

        static string Truncate(string s, int len)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= len ? s : s.Substring(0, len) + "…";
        }

        // ===================== 公共辅助 =====================

        static readonly string[] HeadersXY =
            { "序号", "桩号", "X坐标(北Northing)", "Y坐标(东Easting)" };
        static readonly string[] HeadersXYZ =
            { "序号", "桩号", "X坐标(北Northing)", "Y坐标(东Easting)", "地面高程" };

        static IEnumerable<ObjectId> ModelSpace(Database db, Transaction tr)
        {
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms) yield return id;
        }

        // 起点、每 interval、终点必取
        static IEnumerable<double> Stations(double start, double end, double interval)
        {
            if (interval <= 0) interval = 50.0;
            double s = start;
            while (s < end - 1e-6) { yield return s; s += interval; }
            yield return end;
        }

        static HashSet<string> WantedNames(JsonObject a)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            string one = GetString(a, "name", null);
            if (!string.IsNullOrEmpty(one)) set.Add(one);
            var arr = a["names"] as JsonArray;
            if (arr != null)
                foreach (var n in arr) if (n != null) set.Add(n.GetValue<string>());
            return set.Count == 0 ? null : set;   // null = 全部
        }

        static string ResolveOutDir(JsonObject a, Document doc)
        {
            string dir = GetString(a, "outdir", null);
            if (string.IsNullOrEmpty(dir))
            {
                try { dir = Path.GetDirectoryName(doc.Database.Filename); } catch { }
            }
            if (string.IsNullOrEmpty(dir))
                dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        static string Station(double s)
        {
            int km = (int)Math.Floor(s / 1000.0);
            double rem = s - km * 1000.0;
            return "K" + km.ToString(CultureInfo.InvariantCulture) + "+" +
                   rem.ToString("000.000", CultureInfo.InvariantCulture);
        }

        static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        static string SafeLayer(DBObject o)
        {
            var e = o as Entity;
            try { return e != null ? e.Layer : ""; } catch { return ""; }
        }

        static string SafeFile(Database db)
        {
            try { return db.Filename; } catch { return "(unknown)"; }
        }

        static double Round(double v, int d) { return Math.Round(v, d); }
        static double Round2(double v) { return Math.Round(v, 4); }

        static double GetDouble(JsonObject a, string key, double dflt)
        {
            var n = a[key];
            if (n == null) return dflt;
            try { return n.GetValue<double>(); }
            catch
            {
                double v;
                if (double.TryParse(n.ToString(), NumberStyles.Any,
                        CultureInfo.InvariantCulture, out v)) return v;
                return dflt;
            }
        }

        static bool GetBool(JsonObject a, string key, bool dflt)
        {
            var n = a[key];
            if (n == null) return dflt;
            try { return n.GetValue<bool>(); }
            catch
            {
                bool v;
                return bool.TryParse(n.ToString(), out v) ? v : dflt;
            }
        }

        static string GetString(JsonObject a, string key, string dflt)
        {
            var n = a[key];
            if (n == null) return dflt;
            try { return n.GetValue<string>(); } catch { return n.ToString(); }
        }

        static JsonNode RunTestTwoTierChannel(JsonObject a, Document doc)
        {
            double cx = GetDouble(a, "center_x", 0.0);
            double cy = GetDouble(a, "center_y", 0.0);
            double bottomWidth = GetDouble(a, "bottom_width", 4.0);
            double h1 = GetDouble(a, "h1", 2.5);
            double m1 = GetDouble(a, "m1", 1.5);
            double benchWidth = GetDouble(a, "bench_width", 1.5);
            double benchSlope = GetDouble(a, "bench_slope", 0.0);
            double h2 = GetDouble(a, "h2", 3.0);
            double m2 = GetDouble(a, "m2", 1.75);
            double thickness = GetDouble(a, "lining_thickness", 0.15);
            string layerName = GetString(a, "layer", "C3DF-CHANNEL-2TIER");

            double halfW = bottomWidth / 2.0;

            Point2d pTopLeft2 = new Point2d(cx - (halfW + m1 * h1 + benchWidth + m2 * h2), cy + h1 + benchWidth * benchSlope + h2);
            Point2d pBenchLeftOut = new Point2d(cx - (halfW + m1 * h1 + benchWidth), cy + h1 + benchWidth * benchSlope);
            Point2d pBenchLeftIn = new Point2d(cx - (halfW + m1 * h1), cy + h1);
            Point2d pToeLeft = new Point2d(cx - halfW, cy);
            Point2d pCenterBottom = new Point2d(cx, cy);
            Point2d pToeRight = new Point2d(cx + halfW, cy);
            Point2d pBenchRightIn = new Point2d(cx + halfW + m1 * h1, cy + h1);
            Point2d pBenchRightOut = new Point2d(cx + halfW + m1 * h1 + benchWidth, cy + h1 + benchWidth * benchSlope);
            Point2d pTopRight2 = new Point2d(cx + halfW + m1 * h1 + benchWidth + m2 * h2, cy + h1 + benchWidth * benchSlope + h2);

            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                ObjectId layerId = GridEnsureLayer(tr, db, layerName, 1);

                using (Polyline pl = new Polyline())
                {
                    pl.Layer = layerName;
                    pl.AddVertexAt(0, pTopLeft2, 0, 0, 0);
                    pl.AddVertexAt(1, pBenchLeftOut, 0, 0, 0);
                    pl.AddVertexAt(2, pBenchLeftIn, 0, 0, 0);
                    pl.AddVertexAt(3, pToeLeft, 0, 0, 0);
                    pl.AddVertexAt(4, pToeRight, 0, 0, 0);
                    pl.AddVertexAt(5, pBenchRightIn, 0, 0, 0);
                    pl.AddVertexAt(6, pBenchRightOut, 0, 0, 0);
                    pl.AddVertexAt(7, pTopRight2, 0, 0, 0);

                    btr.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);
                }

                if (thickness > 0)
                {
                    ObjectId liningLayerId = GridEnsureLayer(tr, db, layerName + "-LINING", 3);
                    using (Polyline plLining = new Polyline())
                    {
                        plLining.Layer = layerName + "-LINING";
                        plLining.AddVertexAt(0, new Point2d(pTopLeft2.X, pTopLeft2.Y - thickness), 0, 0, 0);
                        plLining.AddVertexAt(1, new Point2d(pBenchLeftOut.X, pBenchLeftOut.Y - thickness), 0, 0, 0);
                        plLining.AddVertexAt(2, new Point2d(pBenchLeftIn.X, pBenchLeftIn.Y - thickness), 0, 0, 0);
                        plLining.AddVertexAt(3, new Point2d(pToeLeft.X, pToeLeft.Y - thickness), 0, 0, 0);
                        plLining.AddVertexAt(4, new Point2d(pToeRight.X, pToeRight.Y - thickness), 0, 0, 0);
                        plLining.AddVertexAt(5, new Point2d(pBenchRightIn.X, pBenchRightIn.Y - thickness), 0, 0, 0);
                        plLining.AddVertexAt(6, new Point2d(pBenchRightOut.X, pBenchRightOut.Y - thickness), 0, 0, 0);
                        plLining.AddVertexAt(7, new Point2d(pTopRight2.X, pTopRight2.Y - thickness), 0, 0, 0);

                        btr.AppendEntity(plLining);
                        tr.AddNewlyCreatedDBObject(plLining, true);
                    }
                }

                ObjectId textLayerId = GridEnsureLayer(tr, db, layerName + "-TEXT", 2);
                using (MText txt = new MText())
                {
                    txt.Layer = layerName + "-TEXT";
                    txt.Location = new Point3d(cx, cy + 0.5, 0);
                    txt.TextHeight = 0.5;
                    txt.Contents = string.Format("【二级边坡渠道】\\P主槽底宽: {0}m | 一级坡比: 1:{1} (H={2}m) \\P马道宽: {3}m | 二级坡比: 1:{4} (H={5}m) \\P衬砌厚度: {6}m",
                        bottomWidth, m1, h1, benchWidth, m2, h2, thickness);

                    btr.AppendEntity(txt);
                    tr.AddNewlyCreatedDBObject(txt, true);
                }

                tr.Commit();
            }

            double totalTopWidth = (pTopRight2.X - pTopLeft2.X);
            double totalDepth = h1 + benchWidth * benchSlope + h2;

            var res = new JsonObject();
            res["status"] = "success";
            res["channel_type"] = "二级边坡渠道(带主槽和马道平台)";
            res["total_top_width"] = Round(totalTopWidth, 3);
            res["total_depth"] = Round(totalDepth, 3);
            res["bottom_width"] = bottomWidth;
            res["h1"] = h1;
            res["m1"] = m1;
            res["bench_width"] = benchWidth;
            res["h2"] = h2;
            res["m2"] = m2;
            res["lining_thickness"] = thickness;
            res["layer"] = layerName;
            return res;
        }

        static JsonNode RunCreate3DBox(JsonObject a, Document doc)
        {
            double dx = GetDouble(a, "x", 10.0);
            double dy = GetDouble(a, "y", 10.0);
            double dz = GetDouble(a, "z", 5.0);
            double cx = GetDouble(a, "cx", 0.0);
            double cy = GetDouble(a, "cy", 0.0);
            double cz = GetDouble(a, "cz", 0.0);
            string layerName = GetString(a, "layer", "C3DF-BOX-3D");

            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                ObjectId layerId = GridEnsureLayer(tr, db, layerName, 5); // Blue

                using (Solid3d solid = new Solid3d())
                {
                    solid.CreateBox(dx, dy, dz);
                    solid.TransformBy(Autodesk.AutoCAD.Geometry.Matrix3d.Displacement(
                        new Autodesk.AutoCAD.Geometry.Vector3d(cx, cy, cz + dz / 2.0)));
                    solid.Layer = layerName;

                    btr.AppendEntity(solid);
                    tr.AddNewlyCreatedDBObject(solid, true);
                }

                tr.Commit();
            }

            var res = new JsonObject();
            res["status"] = "success";
            res["type"] = "Solid3d Box";
            res["x"] = dx;
            res["y"] = dy;
            res["z"] = dz;
            res["center"] = new JsonArray { cx, cy, cz };
            res["layer"] = layerName;
            return res;
        }

        static JsonNode RunCreate3DSphere(JsonObject a, Document doc)
        {
            double r = GetDouble(a, "radius", 3.0);
            double cx = GetDouble(a, "cx", 0.0);
            double cy = GetDouble(a, "cy", 0.0);
            double cz = GetDouble(a, "cz", 8.0);
            string layerName = GetString(a, "layer", "C3DF-SPHERE-3D");

            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                ObjectId layerId = GridEnsureLayer(tr, db, layerName, 1); // Red

                using (Solid3d solid = new Solid3d())
                {
                    solid.CreateSphere(r);
                    solid.TransformBy(Autodesk.AutoCAD.Geometry.Matrix3d.Displacement(
                        new Autodesk.AutoCAD.Geometry.Vector3d(cx, cy, cz)));
                    solid.Layer = layerName;

                    btr.AppendEntity(solid);
                    tr.AddNewlyCreatedDBObject(solid, true);
                }

                tr.Commit();
            }

            var res = new JsonObject();
            res["status"] = "success";
            res["type"] = "Solid3d Sphere";
            res["radius"] = r;
            res["center"] = new JsonArray { cx, cy, cz };
            res["layer"] = layerName;
            return res;
        }
    }
}
