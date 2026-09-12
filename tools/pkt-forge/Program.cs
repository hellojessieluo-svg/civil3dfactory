using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace PktForge;

/// <summary>
/// PktForge —— 不经过 Subassembly Composer 界面，直接用 C# 生成 Civil 3D 可导入的 .pkt 部件包。
/// v1 内置一种部件：SearchDaylight（搜索范围内探地面，高于原点则按坡比放坡，否则平坡）。
/// .pkt 打包格式说明见 dev_logs\PKT直接打包方法.md。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            string cmd = args.Length > 0 && !args[0].StartsWith('-')
                ? args[0].ToLowerInvariant() : "daylight";

            string left, right;
            if (cmd == "flatdig")
            {
                var spec = FlatDigSpec.Parse(args);
                Directory.CreateDirectory(spec.OutDir);
                left = PktWriter.WriteFlatDigPkt(spec, isLeft: true);
                right = PktWriter.WriteFlatDigPkt(spec, isLeft: false);
            }
            else
            {
                var spec = DaylightSpec.Parse(args);
                Directory.CreateDirectory(spec.OutDir);
                left = PktWriter.WriteDaylightPkt(spec, isLeft: true);
                right = PktWriter.WriteDaylightPkt(spec, isLeft: false);
            }
            Console.WriteLine("生成完成：");
            Console.WriteLine("  " + left + "  (" + new FileInfo(left).Length + " bytes)");
            Console.WriteLine("  " + right + "  (" + new FileInfo(right).Length + " bytes)");
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("参数错误：" + ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(DaylightSpec.Usage);
            Console.Error.WriteLine(FlatDigSpec.Usage);
            return 2;
        }
    }
}

/// <summary>FlatDig 部件参数：清淤联动场景——周边也清到同一高程，堤埝断面即按纵断面高程全宽平底，无放坡。</summary>
internal sealed record FlatDigSpec
{
    public string Name { get; init; } = "FlatDig_v1.0";
    public double LeftWidth { get; init; } = 15;
    public double RightWidth { get; init; } = 15;
    public string Units { get; init; } = "m";
    public string OutDir { get; init; } = ".";
    public string Description { get; init; } =
        "清淤联动拆堤部件：周边同步疏浚到设计高程时使用。从原点（挂在纵断面设计高程上）水平向外" +
        "出固定宽度（Width）的平底开挖链接，不放坡、不找地面。链接码 Top/Datum 供走廊曲面与工程量使用。PktForge 生成。";

    public const string Usage =
        """
        或：
          dotnet run --project tools\pkt-forge -- flatdig [选项]
          --name <名称>          部件名（默认 FlatDig_v1.0）
          --left-width <数值>    左侧平底宽度默认值（默认 15）
          --right-width <数值>   右侧平底宽度默认值（默认 15）
          --units/--out/--desc   同 daylight
        """;

    public static FlatDigSpec Parse(string[] args)
    {
        var spec = new FlatDigSpec();
        for (int i = 1; i < args.Length; i += 2)
        {
            string key = args[i];
            if (i + 1 >= args.Length) throw new ArgumentException(key + " 缺少取值");
            string val = args[i + 1];
            spec = key switch
            {
                "--name" => spec with { Name = val },
                "--left-width" => spec with { LeftWidth = Num(key, val) },
                "--right-width" => spec with { RightWidth = Num(key, val) },
                "--units" => spec with { Units = val },
                "--out" => spec with { OutDir = val },
                "--desc" => spec with { Description = val },
                _ => throw new ArgumentException("未知选项：" + key)
            };
        }
        if (spec.LeftWidth <= 0 || spec.RightWidth <= 0)
            throw new ArgumentException("宽度必须为正数");
        return spec;
    }

    private static double Num(string key, string val)
    {
        if (!double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            throw new ArgumentException(key + " 不是数字：" + val);
        return d;
    }
}

/// <summary>SearchDaylight 部件的生成参数（命令行传入，同时作为 pkt 里参数的默认值）。</summary>
internal sealed record DaylightSpec
{
    public string Name { get; init; } = "SearchDaylight_v1.0";
    public double LeftOffset { get; init; } = 10;
    public double RightOffset { get; init; } = 10;
    public double SlopeH { get; init; } = 5;
    public string Units { get; init; } = "m";
    public string OutDir { get; init; } = ".";
    public string Description { get; init; } =
        "挖堤搜索部件：从原点沿 X 轴按原点高程水平搜索原地面交点。" +
        "范围端点地面仍高于原点→平底挖到范围端点（DigStart 即开挖起点）再按固定坡比 1:SlopeH 向上放坡出地面；" +
        "范围内地面已回落到原点高程以下→平坡延伸至与地面的交点收口；" +
        "原点与范围端点地面都低于原点→该桩号不产生几何。" +
        "目标曲面 EG_Surface 必须映射原地面。PktForge 生成。";

    public const string Usage =
        """
        用法：
          dotnet run --project tools\pkt-forge -- daylight [选项]

        选项（都有默认值）：
          --name <名称>           部件名（默认 SearchDaylight_v1.0），输出 <名称>_LEFT.pkt / <名称>_RIGHT.pkt
          --left-offset <数值>    左侧搜索范围默认值（默认 10）
          --right-offset <数值>   右侧搜索范围默认值（默认 10）
          --slope-h <数值>        放坡坡比 1:SlopeH 的 SlopeH 默认值（默认 5）
          --units <单位>          单位（默认 m）
          --out <目录>            输出目录（默认当前目录）
          --desc <文字>           部件描述（写进 Civil 3D 工具提示）
        """;

    public static DaylightSpec Parse(string[] args)
    {
        var spec = new DaylightSpec();
        int i = 0;
        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            if (!string.Equals(args[0], "daylight", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("未知子命令：" + args[0] + "（目前只有 daylight）");
            i = 1;
        }
        for (; i < args.Length; i += 2)
        {
            string key = args[i];
            if (i + 1 >= args.Length) throw new ArgumentException(key + " 缺少取值");
            string val = args[i + 1];
            spec = key switch
            {
                "--name" => spec with { Name = val },
                "--left-offset" => spec with { LeftOffset = ParseNum(key, val) },
                "--right-offset" => spec with { RightOffset = ParseNum(key, val) },
                "--slope-h" => spec with { SlopeH = ParseNum(key, val) },
                "--units" => spec with { Units = val },
                "--out" => spec with { OutDir = val },
                "--desc" => spec with { Description = val },
                _ => throw new ArgumentException("未知选项：" + key)
            };
        }
        if (spec.LeftOffset <= 0 || spec.RightOffset <= 0)
            throw new ArgumentException("搜索范围必须为正数");
        if (spec.SlopeH <= 0)
            throw new ArgumentException("坡比 SlopeH 必须为正数");
        return spec;
    }

    private static double ParseNum(string key, string val)
    {
        if (!double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            throw new ArgumentException(key + " 不是数字：" + val);
        return d;
    }
}

/// <summary>
/// 按 Civil 3D 2025 / SAC 13.7 的 .pkt 结构打包：
/// zip 内为同一 GUID 命名的 .xaml/.atc/.cfg/.cdmd/.emd/.pvd 加 [Content_Types].xml。
/// .xaml 与 [Content_Types].xml 带 UTF-8 BOM，其余不带（与 SAC 原生输出一致）。
/// </summary>
internal static class PktWriter
{
    public static string WriteDaylightPkt(DaylightSpec spec, bool isLeft)
    {
        string side = isLeft ? "Left" : "Right";
        string toolName = SanitizeIdentifier(spec.Name) + "_" + side;
        string guid = Guid.NewGuid().ToString("N");
        double offset = isLeft ? spec.LeftOffset : spec.RightOffset;

        string outPath = Path.Combine(spec.OutDir, spec.Name + "_" + side.ToUpperInvariant() + ".pkt");

        var bomUtf8 = new UTF8Encoding(true);
        var utf8 = new UTF8Encoding(false);

        File.Delete(outPath);
        using var zip = ZipFile.Open(outPath, ZipArchiveMode.Create);
        AddEntry(zip, guid + ".xaml", DaylightXaml.Build(spec, isLeft, offset), bomUtf8);
        AddEntry(zip, guid + ".atc", BuildAtc(spec, toolName, guid, isLeft, offset), utf8);
        AddEntry(zip, guid + ".cfg", Cfg, utf8);
        AddEntry(zip, guid + ".cdmd", Cdmd, utf8);
        AddEntry(zip, guid + ".emd", Emd, utf8);
        AddEntry(zip, guid + ".pvd", Pvd, utf8);
        AddEntry(zip, "[Content_Types].xml", ContentTypes, bomUtf8);
        return outPath;
    }

    public static string WriteFlatDigPkt(FlatDigSpec spec, bool isLeft)
    {
        string side = isLeft ? "Left" : "Right";
        string toolName = SanitizeIdentifier(spec.Name) + "_" + side;
        string guid = Guid.NewGuid().ToString("N");
        double width = isLeft ? spec.LeftWidth : spec.RightWidth;
        int sideVal = isLeft ? 1 : 0;

        string outPath = Path.Combine(spec.OutDir, spec.Name + "_" + side.ToUpperInvariant() + ".pkt");

        string paramsXml =
            $$"""
                        <Side DataType="long" TypeInfo="16" DisplayName="Side" Description="Side">{{sideVal}}<Enum><Left DisplayName="Left">1</Left><Right DisplayName="Right">0</Right></Enum></Side>
                        <Width DataType="double" TypeInfo="16" DisplayName="Width" Description="平底开挖单侧宽度">{{N(width)}}</Width>
            """;

        var bomUtf8 = new UTF8Encoding(true);
        var utf8 = new UTF8Encoding(false);
        File.Delete(outPath);
        using var zip = ZipFile.Open(outPath, ZipArchiveMode.Create);
        AddEntry(zip, guid + ".xaml", FlatDigXaml.Build(spec, isLeft, width), bomUtf8);
        AddEntry(zip, guid + ".atc",
            BuildAtcXml(toolName, XmlEscape(spec.Description), guid, paramsXml, spec.Units), utf8);
        AddEntry(zip, guid + ".cfg", Cfg, utf8);
        AddEntry(zip, guid + ".cdmd", Cdmd, utf8);
        AddEntry(zip, guid + ".emd", Emd, utf8);
        AddEntry(zip, guid + ".pvd", Pvd, utf8);
        AddEntry(zip, "[Content_Types].xml", ContentTypes, bomUtf8);
        return outPath;
    }

    /// <summary>.atc 通用骨架：工具名、描述、参数段（各部件自己拼）。</summary>
    internal static string BuildAtcXml(string toolName, string escapedDesc, string guid, string paramsXml, string units)
    {
        return $$"""
            <?xml version="1.0"?>
            <Category xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <ItemID idValue="{{{Guid.NewGuid()}}}" />
              <Properties>
                <ItemName>Category1</ItemName>
                <Images>
                  <Image cx="93" cy="123" />
                </Images>
              </Properties>
              <CustomData />
              <Source />
              <Palettes />
              <Packages />
              <Tools>
                <Tool Name="{{toolName}}">
                  <ItemID idValue="{{{Guid.NewGuid()}}}" />
                  <Properties>
                    <ItemName>{{toolName}}</ItemName>
                    <Images>
                      <Image cx="64" cy="64" />
                    </Images>
                    <Description>{{escapedDesc}}</Description>
                    <ToolTip>{{escapedDesc}}\nVersion: 1.0</ToolTip>
                    <Help>
                      <HelpFile />
                      <HelpCommand />
                      <HelpData />
                    </Help>
                  </Properties>
                  <Source />
                  <StockToolRef idValue="{7F55AAC0-0256-48D7-BFA5-914702663FDE}" />
                  <Data>
                    <AeccDbSubassembly>
                      <GeometryGenerateMode>UseDotNet</GeometryGenerateMode>
                      <DotNetClass Assembly="{{guid}}.xaml">Subassembly.{{toolName}}</DotNetClass>
                      <Version>1.0</Version>
                      <Params>
            {{paramsXml}}
                      </Params>
                    </AeccDbSubassembly>
                    <Units>{{units}}</Units>
                  </Data>
                </Tool>
              </Tools>
              <StockTools />
            </Category>
            """;
    }

    private static void AddEntry(ZipArchive zip, string name, string content, Encoding encoding)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = encoding.GetBytes(content);
        var preamble = encoding.GetPreamble();
        stream.Write(preamble, 0, preamble.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    internal static string N(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// SAC/Civil 3D 对部件内部名（SAName）的要求：字母开头，之后只能字母/数字/下划线。
    /// 文件名不受此限制，所以只在生成内部名时合法化：非法字符替换为下划线（如 v1.0 → v1_0）。
    /// </summary>
    internal static string SanitizeIdentifier(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        string result = sb.ToString().Trim('_');
        if (result.Length == 0 || !char.IsLetter(result[0])) result = "SA_" + result;
        return result;
    }

    private static string XmlEscape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>.atc：Civil 3D 导入时读取的工具目录（部件名、描述、参数及默认值）。目标参数不在此列，只在 XAML 里。</summary>
    private static string BuildAtc(DaylightSpec spec, string toolName, string guid, bool isLeft, double offset)
    {
        string desc = XmlEscape(spec.Description);
        int sideVal = isLeft ? 1 : 0;
        return $$"""
            <?xml version="1.0"?>
            <Category xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <ItemID idValue="{{{Guid.NewGuid()}}}" />
              <Properties>
                <ItemName>Category1</ItemName>
                <Images>
                  <Image cx="93" cy="123" />
                </Images>
              </Properties>
              <CustomData />
              <Source />
              <Palettes />
              <Packages />
              <Tools>
                <Tool Name="{{toolName}}">
                  <ItemID idValue="{{{Guid.NewGuid()}}}" />
                  <Properties>
                    <ItemName>{{toolName}}</ItemName>
                    <Images>
                      <Image cx="64" cy="64" />
                    </Images>
                    <Description>{{desc}}</Description>
                    <ToolTip>{{desc}}\nVersion: 1.0</ToolTip>
                    <Help>
                      <HelpFile />
                      <HelpCommand />
                      <HelpData />
                    </Help>
                  </Properties>
                  <Source />
                  <StockToolRef idValue="{7F55AAC0-0256-48D7-BFA5-914702663FDE}" />
                  <Data>
                    <AeccDbSubassembly>
                      <GeometryGenerateMode>UseDotNet</GeometryGenerateMode>
                      <DotNetClass Assembly="{{guid}}.xaml">Subassembly.{{toolName}}</DotNetClass>
                      <Version>1.0</Version>
                      <Params>
                        <Side DataType="long" TypeInfo="16" DisplayName="Side" Description="Side">{{sideVal}}<Enum><Left DisplayName="Left">1</Left><Right DisplayName="Right">0</Right></Enum></Side>
                        <SearchOffset DataType="double" TypeInfo="16" DisplayName="SearchOffset" Description="搜索范围：沿原点高程水平向外搜索原地面交点的最大距离，也是平底开挖的最大宽度">{{N(offset)}}</SearchOffset>
                        <SlopeH DataType="double" TypeInfo="16" DisplayName="SlopeH" Description="开挖放坡坡比 1:SlopeH（每 1 单位高差的水平距离）">{{N(spec.SlopeH)}}</SlopeH>
                      </Params>
                    </AeccDbSubassembly>
                    <Units>{{spec.Units}}</Units>
                  </Data>
                </Tool>
              </Tools>
              <StockTools />
            </Category>
            """;
    }

    private const string Cfg =
        """
        <?xml version="1.0"?>
        <Configuration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <CreatedWith>
            <ProductName>Autodesk Subassembly Composer</ProductName>
            <Version>ForVail</Version>
            <VersionNumber>13.7.145.0</VersionNumber>
          </CreatedWith>
        </Configuration>
        """;

    private const string Cdmd =
        """
        <?xml version="1.0"?>
        <CodeData xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <CodeItems />
        </CodeData>
        """;

    private const string Emd =
        """
        <?xml version="1.0"?>
        <EnumData xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <EnumDatas>
            <Groups />
            <DefinedVariables />
          </EnumDatas>
        </EnumData>
        """;

    private const string Pvd =
        """
        <?xml version="1.0"?>
        <PreviewData xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <Superelevation>
            <CrossSlopes>
              <PreviewCrossSlope CrossSegmentType="LeftInsideLane" Slope="-0.02" IsDefined="true" />
              <PreviewCrossSlope CrossSegmentType="LeftInsideShoulder" Slope="-0.05" IsDefined="true" />
              <PreviewCrossSlope CrossSegmentType="LeftOutsideLane" Slope="-0.02" IsDefined="true" />
              <PreviewCrossSlope CrossSegmentType="LeftOutsideShoulder" Slope="-0.05" IsDefined="true" />
              <PreviewCrossSlope CrossSegmentType="RightInsideLane" Slope="-0.02" IsDefined="true" />
              <PreviewCrossSlope CrossSegmentType="RightInsideShoulder" Slope="-0.05" IsDefined="true" />
              <PreviewCrossSlope CrossSegmentType="RightOutsideLane" Slope="-0.02" IsDefined="true" />
              <PreviewCrossSlope CrossSegmentType="RightOutsideShoulder" Slope="-0.05" IsDefined="true" />
            </CrossSlopes>
          </Superelevation>
          <Cant>
            <CantParams>
              <PreviewCantParam Name="CantPivotType" Value="CenterLine" />
              <PreviewCantParam Name="LeftRail" Value="" />
              <PreviewCantParam Name="LeftRailDeltaElevation" Value="0" />
              <PreviewCantParam Name="RightRail" Value="" />
              <PreviewCantParam Name="RightRailDeltaElevation" Value="0" />
            </CantParams>
          </Cant>
        </PreviewData>
        """;

    private const string ContentTypes =
        """<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="atc" ContentType="" /><Default Extension="cfg" ContentType="" /><Default Extension="xaml" ContentType="" /><Default Extension="pvd" ContentType="" /><Default Extension="emd" ContentType="" /><Default Extension="cdmd" ContentType="" /></Types>""";
}

/// <summary>FlatDig 的 WF4 流程图：P1 原点 → P2 (Width,0) → 平底链接，无曲面目标。</summary>
internal static class FlatDigXaml
{
    public static string Build(FlatDigSpec spec, bool isLeft, double width)
    {
        int sideVal = isLeft ? 1 : 0;
        string sideName = isLeft ? "Left" : "Right";
        string n(double d) => PktWriter.N(d);

        return $$"""
            <Activity mc:Ignorable="sads sap" x:Class="Subassembly" this:Subassembly.Side="[new EnumType({{sideVal}}, &quot;{{sideName}}&quot;, &quot;Side&quot;)]" this:Subassembly.Width="{{n(width)}}"
             xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
             xmlns:asa="clr-namespace:Autodesk.SubassemblyComposer.API;assembly=Subassembly.API"
             xmlns:asa1="clr-namespace:Autodesk.SubassemblyComposer.API;assembly=Subassembly.WorkflowEngine"
             xmlns:asa2="clr-namespace:Autodesk.SubassemblyComposer.ActivityLibrary;assembly=Subassembly.ActivityLibrary"
             xmlns:asw="clr-namespace:Autodesk.SubassemblyComposer.WorkflowEngine;assembly=Subassembly.WorkflowEngine"
             xmlns:av="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             xmlns:mva="clr-namespace:Microsoft.VisualBasic.Activities;assembly=System.Activities"
             xmlns:s="clr-namespace:System;assembly=mscorlib"
             xmlns:s1="clr-namespace:System;assembly=System.Core"
             xmlns:s2="clr-namespace:System;assembly=System"
             xmlns:s3="clr-namespace:System;assembly=System.Runtime.WindowsRuntime"
             xmlns:sa="clr-namespace:System.Activities;assembly=System.Activities"
             xmlns:sads="http://schemas.microsoft.com/netfx/2010/xaml/activities/debugger"
             xmlns:sap="http://schemas.microsoft.com/netfx/2009/xaml/activities/presentation"
             xmlns:scg="clr-namespace:System.Collections.Generic;assembly=mscorlib"
             xmlns:this="clr-namespace:"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <x:Members>
                <x:Property Name="Geometry" Type="InOutArgument(asw:Geometry)" />
                <x:Property Name="SubassemblyErrorCenter" Type="InOutArgument(asw:SubassemblyErrorCenter)" />
                <x:Property Name="SubassemblyRunMode" Type="InOutArgument(asw:SubassemblyRunMode)" />
                <x:Property Name="Side" Type="InArgument(asw:EnumType)">
                  <x:Property.Attributes>
                    <asw:EnabledFlag2Attribute EnabledFlag="True" />
                  </x:Property.Attributes>
                </x:Property>
                <x:Property Name="Width" Type="InArgument(x:Double)">
                  <x:Property.Attributes>
                    <asw:EnabledFlag2Attribute EnabledFlag="True" />
                    <asw:Description2Attribute Description="平底开挖单侧宽度" />
                  </x:Property.Attributes>
                </x:Property>
              </x:Members>
              <sap:VirtualizedContainerService.HintSize>1188,809</sap:VirtualizedContainerService.HintSize>
              <mva:VisualBasic.Settings>Assembly references and imported namespaces serialized as XML namespaces</mva:VisualBasic.Settings>
              <Flowchart sap:VirtualizedContainerService.HintSize="1148,769" mva:VisualBasic.Settings="Assembly references and imported namespaces serialized as XML namespaces">
                <Flowchart.Variables>
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(41, &quot;AwayFromCrown&quot;)]" Modifiers="ReadOnly" Name="AwayFromCrown" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(1, &quot;Left&quot;)]" Modifiers="ReadOnly" Name="Left" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(20, &quot;LeftInsideLane&quot;)]" Modifiers="ReadOnly" Name="LeftInsideLane" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(21, &quot;LeftInsideShoulder&quot;)]" Modifiers="ReadOnly" Name="LeftInsideShoulder" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(22, &quot;LeftOutsideLane&quot;)]" Modifiers="ReadOnly" Name="LeftOutsideLane" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(23, &quot;LeftOutsideShoulder&quot;)]" Modifiers="ReadOnly" Name="LeftOutsideShoulder" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(11, &quot;No&quot;)]" Modifiers="ReadOnly" Name="No" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(-1, &quot;None&quot;)]" Modifiers="ReadOnly" Name="None" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(0, &quot;Right&quot;)]" Modifiers="ReadOnly" Name="Right" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(24, &quot;RightInsideLane&quot;)]" Modifiers="ReadOnly" Name="RightInsideLane" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(25, &quot;RightInsideShoulder&quot;)]" Modifiers="ReadOnly" Name="RightInsideShoulder" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(26, &quot;RightOutsideLane&quot;)]" Modifiers="ReadOnly" Name="RightOutsideLane" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(27, &quot;RightOutsideShoulder&quot;)]" Modifiers="ReadOnly" Name="RightOutsideShoulder" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(30, &quot;Supported&quot;)]" Modifiers="ReadOnly" Name="Supported" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(40, &quot;TowardsCrown&quot;)]" Modifiers="ReadOnly" Name="TowardsCrown" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(31, &quot;Unsupported&quot;)]" Modifiers="ReadOnly" Name="Unsupported" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(10, &quot;Yes&quot;)]" Modifiers="ReadOnly" Name="Yes" />
                </Flowchart.Variables>
                <sap:WorkflowViewStateService.ViewState>
                  <scg:Dictionary x:TypeArguments="x:String, x:Object">
                    <x:Boolean x:Key="IsExpanded">False</x:Boolean>
                    <av:Point x:Key="ShapeLocation">270,2.5</av:Point>
                    <av:Size x:Key="ShapeSize">60,75</av:Size>
                    <av:PointCollection x:Key="ConnectorLocation">300,77.5 300,99</av:PointCollection>
                  </scg:Dictionary>
                </sap:WorkflowViewStateService.ViewState>
                <Flowchart.StartNode>
                  <FlowStep x:Name="__ReferenceID0">
                    <sap:WorkflowViewStateService.ViewState>
                      <scg:Dictionary x:TypeArguments="x:String, x:Object">
                        <av:Point x:Key="ShapeLocation">200,99</av:Point>
                        <av:Size x:Key="ShapeSize">200,22</av:Size>
                        <av:PointCollection x:Key="ConnectorLocation">300,121 300,139</av:PointCollection>
                      </scg:Dictionary>
                    </sap:WorkflowViewStateService.ViewState>
                    <asa2:CreatePoint AutoLinkCodes="{x:Null}" AutoLinkGeometryName="{x:Null}" FromPoint="{x:Null}" ActivityId="1" ApplyAOR="False" AutoLink="False" DisplayName="P1 原点" Geometry="[Geometry]" sap:VirtualizedContainerService.HintSize="200,22" PointNumber="P1" Positioning="DeltaXAndDeltaY" ShowErrors="True" Side="[Side]" SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]">
                      <asa2:CreatePoint.Arguments>
                        <InArgument x:TypeArguments="x:Double" x:Key="DeltaX1">0</InArgument>
                        <InArgument x:TypeArguments="x:Double" x:Key="DeltaY1">0</InArgument>
                      </asa2:CreatePoint.Arguments>
                      <asa2:CreatePoint.PointCodes>
                        <scg:List x:TypeArguments="InArgument(x:String)" Capacity="4">
                          <InArgument x:TypeArguments="x:String">Origin</InArgument>
                        </scg:List>
                      </asa2:CreatePoint.PointCodes>
                      <sap:WorkflowViewStateService.ViewState>
                        <scg:Dictionary x:TypeArguments="x:String, x:Object">
                          <x:Boolean x:Key="IsExpanded">True</x:Boolean>
                        </scg:Dictionary>
                      </sap:WorkflowViewStateService.ViewState>
                    </asa2:CreatePoint>
                    <FlowStep.Next>
                      <FlowStep x:Name="__ReferenceID1">
                        <sap:WorkflowViewStateService.ViewState>
                          <scg:Dictionary x:TypeArguments="x:String, x:Object">
                            <av:Point x:Key="ShapeLocation">200,139</av:Point>
                            <av:Size x:Key="ShapeSize">200,22</av:Size>
                            <av:PointCollection x:Key="ConnectorLocation">300,161 300,189</av:PointCollection>
                          </scg:Dictionary>
                        </sap:WorkflowViewStateService.ViewState>
                        <asa2:CreatePoint AutoLinkCodes="{x:Null}" AutoLinkGeometryName="{x:Null}" ActivityId="2" ApplyAOR="False" AutoLink="False" DisplayName="P2 平底端点" FromPoint="P1" Geometry="[Geometry]" sap:VirtualizedContainerService.HintSize="200,22" PointNumber="P2" Positioning="DeltaXAndDeltaY" ShowErrors="True" Side="[Side]" SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]">
                          <asa2:CreatePoint.Arguments>
                            <InArgument x:TypeArguments="x:Double" x:Key="DeltaX1">[Width]</InArgument>
                            <InArgument x:TypeArguments="x:Double" x:Key="DeltaY1">0</InArgument>
                          </asa2:CreatePoint.Arguments>
                          <asa2:CreatePoint.PointCodes>
                            <scg:List x:TypeArguments="InArgument(x:String)" Capacity="4">
                              <InArgument x:TypeArguments="x:String">DigEnd</InArgument>
                            </scg:List>
                          </asa2:CreatePoint.PointCodes>
                          <sap:WorkflowViewStateService.ViewState>
                            <scg:Dictionary x:TypeArguments="x:String, x:Object">
                              <x:Boolean x:Key="IsExpanded">True</x:Boolean>
                            </scg:Dictionary>
                          </sap:WorkflowViewStateService.ViewState>
                        </asa2:CreatePoint>
                        <FlowStep.Next>
                          <FlowStep x:Name="__ReferenceID2">
                            <sap:WorkflowViewStateService.ViewState>
                              <scg:Dictionary x:TypeArguments="x:String, x:Object">
                                <av:Point x:Key="ShapeLocation">200,189</av:Point>
                                <av:Size x:Key="ShapeSize">200,22</av:Size>
                              </scg:Dictionary>
                            </sap:WorkflowViewStateService.ViewState>
                            <asa2:CreateLink ActivityId="3" ApplyAOR="False" DisplayName="L1 平底开挖" EndPoint="P2" Geometry="[Geometry]" sap:VirtualizedContainerService.HintSize="200,22" IsEnabled="True" LinkNumber="L1" ShowErrors="True" StartPoint="P1" SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]">
                              <asa2:CreateLink.LinkCodes>
                                <scg:List x:TypeArguments="InArgument(x:String)" Capacity="4">
                                  <InArgument x:TypeArguments="x:String">Top</InArgument>
                                  <InArgument x:TypeArguments="x:String">Datum</InArgument>
                                  <InArgument x:TypeArguments="x:String">Flat</InArgument>
                                  <InArgument x:TypeArguments="x:String">Cut</InArgument>
                                </scg:List>
                              </asa2:CreateLink.LinkCodes>
                              <sap:WorkflowViewStateService.ViewState>
                                <scg:Dictionary x:TypeArguments="x:String, x:Object">
                                  <x:Boolean x:Key="IsExpanded">True</x:Boolean>
                                </scg:Dictionary>
                              </sap:WorkflowViewStateService.ViewState>
                            </asa2:CreateLink>
                          </FlowStep>
                        </FlowStep.Next>
                      </FlowStep>
                    </FlowStep.Next>
                  </FlowStep>
                </Flowchart.StartNode>
                <x:Reference>__ReferenceID0</x:Reference>
                <x:Reference>__ReferenceID1</x:Reference>
                <x:Reference>__ReferenceID2</x:Reference>
              </Flowchart>
            </Activity>
            """;
    }
}

/// <summary>
/// SearchDaylight 的 WF4 流程图 XAML。
/// 结构与 SAC 导出的 RiverSlope 模板逐项对齐：x:Class=Subassembly、x:Members 声明参数与目标、
/// Flowchart 内 CreatePoint/CreateAuxPoint/CreateLink/FlowDecision 节点链。
/// 各 Positioning 模式的参数键名后缀是固定的：DeltaXAndDeltaY=1、SlopeToSurface=3、DeltaXOnSurface=4、AngleAndDeltaX=6。
/// </summary>
internal static class DaylightXaml
{
    public static string Build(DaylightSpec spec, bool isLeft, double offset)
    {
        int sideVal = isLeft ? 1 : 0;
        string sideName = isLeft ? "Left" : "Right";
        string n(double d) => PktWriter.N(d);

        return $$"""
            <Activity mc:Ignorable="sads sap" x:Class="Subassembly" this:Subassembly.Side="[new EnumType({{sideVal}}, &quot;{{sideName}}&quot;, &quot;Side&quot;)]" this:Subassembly.SearchOffset="{{n(offset)}}" this:Subassembly.SlopeH="{{n(spec.SlopeH)}}" this:Subassembly.EG_Surface="[new PreviewSurfaceTarget(&quot;EG_Surface&quot;, True, 1000, 3, -1000, 3)]"
             xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
             xmlns:asa="clr-namespace:Autodesk.SubassemblyComposer.API;assembly=Subassembly.API"
             xmlns:asa1="clr-namespace:Autodesk.SubassemblyComposer.API;assembly=Subassembly.WorkflowEngine"
             xmlns:asa2="clr-namespace:Autodesk.SubassemblyComposer.ActivityLibrary;assembly=Subassembly.ActivityLibrary"
             xmlns:asw="clr-namespace:Autodesk.SubassemblyComposer.WorkflowEngine;assembly=Subassembly.WorkflowEngine"
             xmlns:av="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             xmlns:mva="clr-namespace:Microsoft.VisualBasic.Activities;assembly=System.Activities"
             xmlns:s="clr-namespace:System;assembly=mscorlib"
             xmlns:s1="clr-namespace:System;assembly=System.Core"
             xmlns:s2="clr-namespace:System;assembly=System"
             xmlns:s3="clr-namespace:System;assembly=System.Runtime.WindowsRuntime"
             xmlns:sa="clr-namespace:System.Activities;assembly=System.Activities"
             xmlns:sads="http://schemas.microsoft.com/netfx/2010/xaml/activities/debugger"
             xmlns:sap="http://schemas.microsoft.com/netfx/2009/xaml/activities/presentation"
             xmlns:scg="clr-namespace:System.Collections.Generic;assembly=mscorlib"
             xmlns:this="clr-namespace:"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <x:Members>
                <x:Property Name="Geometry" Type="InOutArgument(asw:Geometry)" />
                <x:Property Name="SubassemblyErrorCenter" Type="InOutArgument(asw:SubassemblyErrorCenter)" />
                <x:Property Name="SubassemblyRunMode" Type="InOutArgument(asw:SubassemblyRunMode)" />
                <x:Property Name="Side" Type="InArgument(asw:EnumType)">
                  <x:Property.Attributes>
                    <asw:EnabledFlag2Attribute EnabledFlag="True" />
                  </x:Property.Attributes>
                </x:Property>
                <x:Property Name="SearchOffset" Type="InArgument(x:Double)">
                  <x:Property.Attributes>
                    <asw:EnabledFlag2Attribute EnabledFlag="True" />
                    <asw:Description2Attribute Description="搜索范围：沿原点高程水平向外搜索原地面交点的最大距离，也是平底开挖的最大宽度" />
                  </x:Property.Attributes>
                </x:Property>
                <x:Property Name="SlopeH" Type="InArgument(x:Double)">
                  <x:Property.Attributes>
                    <asw:EnabledFlag2Attribute EnabledFlag="True" />
                    <asw:Description2Attribute Description="放坡坡比 1:SlopeH（每 1 单位高差的水平距离）" />
                  </x:Property.Attributes>
                </x:Property>
                <x:Property Name="EG_Surface" Type="InArgument(asw:SurfaceTarget)">
                  <x:Property.Attributes>
                    <asw:EnabledFlag2Attribute EnabledFlag="True" />
                    <asw:Description2Attribute Description="原地面目标曲面" />
                  </x:Property.Attributes>
                </x:Property>
              </x:Members>
              <sap:VirtualizedContainerService.HintSize>1188,809</sap:VirtualizedContainerService.HintSize>
              <mva:VisualBasic.Settings>Assembly references and imported namespaces serialized as XML namespaces</mva:VisualBasic.Settings>
              <Flowchart sap:VirtualizedContainerService.HintSize="1148,769" mva:VisualBasic.Settings="Assembly references and imported namespaces serialized as XML namespaces">
                <Flowchart.Variables>
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(41, &quot;AwayFromCrown&quot;)]" Modifiers="ReadOnly" Name="AwayFromCrown" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(1, &quot;Left&quot;)]" Modifiers="ReadOnly" Name="Left" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(20, &quot;LeftInsideLane&quot;)]" Modifiers="ReadOnly" Name="LeftInsideLane" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(21, &quot;LeftInsideShoulder&quot;)]" Modifiers="ReadOnly" Name="LeftInsideShoulder" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(22, &quot;LeftOutsideLane&quot;)]" Modifiers="ReadOnly" Name="LeftOutsideLane" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(23, &quot;LeftOutsideShoulder&quot;)]" Modifiers="ReadOnly" Name="LeftOutsideShoulder" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(11, &quot;No&quot;)]" Modifiers="ReadOnly" Name="No" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(-1, &quot;None&quot;)]" Modifiers="ReadOnly" Name="None" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(0, &quot;Right&quot;)]" Modifiers="ReadOnly" Name="Right" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(24, &quot;RightInsideLane&quot;)]" Modifiers="ReadOnly" Name="RightInsideLane" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(25, &quot;RightInsideShoulder&quot;)]" Modifiers="ReadOnly" Name="RightInsideShoulder" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(26, &quot;RightOutsideLane&quot;)]" Modifiers="ReadOnly" Name="RightOutsideLane" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(27, &quot;RightOutsideShoulder&quot;)]" Modifiers="ReadOnly" Name="RightOutsideShoulder" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(30, &quot;Supported&quot;)]" Modifiers="ReadOnly" Name="Supported" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(40, &quot;TowardsCrown&quot;)]" Modifiers="ReadOnly" Name="TowardsCrown" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(31, &quot;Unsupported&quot;)]" Modifiers="ReadOnly" Name="Unsupported" />
                  <Variable x:TypeArguments="asw:EnumType" Default="[new EnumType(10, &quot;Yes&quot;)]" Modifiers="ReadOnly" Name="Yes" />
                </Flowchart.Variables>
                <sap:WorkflowViewStateService.ViewState>
                  <scg:Dictionary x:TypeArguments="x:String, x:Object">
                    <x:Boolean x:Key="IsExpanded">False</x:Boolean>
                    <av:Point x:Key="ShapeLocation">270,2.5</av:Point>
                    <av:Size x:Key="ShapeSize">60,75</av:Size>
                    <av:PointCollection x:Key="ConnectorLocation">300,77.5 300,99</av:PointCollection>
                  </scg:Dictionary>
                </sap:WorkflowViewStateService.ViewState>
                <Flowchart.StartNode>
                  <FlowStep x:Name="__ReferenceID0">
                    <sap:WorkflowViewStateService.ViewState>
                      <scg:Dictionary x:TypeArguments="x:String, x:Object">
                        <av:Point x:Key="ShapeLocation">200,99</av:Point>
                        <av:Size x:Key="ShapeSize">200,22</av:Size>
                        <av:PointCollection x:Key="ConnectorLocation">300,121 300,139</av:PointCollection>
                      </scg:Dictionary>
                    </sap:WorkflowViewStateService.ViewState>
                    <asa2:CreatePoint AutoLinkCodes="{x:Null}" AutoLinkGeometryName="{x:Null}" FromPoint="{x:Null}" ActivityId="1" ApplyAOR="False" AutoLink="False" DisplayName="P1 原点" Geometry="[Geometry]" sap:VirtualizedContainerService.HintSize="200,22" PointNumber="P1" Positioning="DeltaXAndDeltaY" ShowErrors="True" Side="[Side]" SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]">
                      <asa2:CreatePoint.Arguments>
                        <InArgument x:TypeArguments="x:Double" x:Key="DeltaX1">0</InArgument>
                        <InArgument x:TypeArguments="x:Double" x:Key="DeltaY1">0</InArgument>
                      </asa2:CreatePoint.Arguments>
                      <asa2:CreatePoint.PointCodes>
                        <scg:List x:TypeArguments="InArgument(x:String)" Capacity="4">
                          <InArgument x:TypeArguments="x:String">Origin</InArgument>
                        </scg:List>
                      </asa2:CreatePoint.PointCodes>
                      <sap:WorkflowViewStateService.ViewState>
                        <scg:Dictionary x:TypeArguments="x:String, x:Object">
                          <x:Boolean x:Key="IsExpanded">True</x:Boolean>
                        </scg:Dictionary>
                      </sap:WorkflowViewStateService.ViewState>
                    </asa2:CreatePoint>
                    <FlowStep.Next>
                      <FlowStep x:Name="__ReferenceID1">
                        <sap:WorkflowViewStateService.ViewState>
                          <scg:Dictionary x:TypeArguments="x:String, x:Object">
                            <av:Point x:Key="ShapeLocation">200,139</av:Point>
                            <av:Size x:Key="ShapeSize">200,22</av:Size>
                            <av:PointCollection x:Key="ConnectorLocation">300,161 300,186.5</av:PointCollection>
                          </scg:Dictionary>
                        </sap:WorkflowViewStateService.ViewState>
                        <asa2:CreateAuxPoint AutoLinkCodes="{x:Null}" AutoLinkGeometryName="{x:Null}" PointCodes="{x:Null}" ActivityId="2" ApplyAOR="False" AutoLink="False" DisplayName="AP1 范围端点探地面" FromPoint="P1" Geometry="[Geometry]" sap:VirtualizedContainerService.HintSize="200,22" PointNumber="AP1" Positioning="DeltaXOnSurface" ShowErrors="True" Side="[Side]" SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]">
                          <asa2:CreateAuxPoint.Arguments>
                            <InArgument x:TypeArguments="x:Double" x:Key="DeltaX4">[SearchOffset]</InArgument>
                            <InArgument x:TypeArguments="asw:SurfaceTarget" x:Key="SurfaceTarget4">[EG_Surface]</InArgument>
                            <x:Null x:Key="OffsetTarget4" />
                            <InArgument x:TypeArguments="x:Double" x:Key="DeltaYForLayout4">3</InArgument>
                          </asa2:CreateAuxPoint.Arguments>
                          <sap:WorkflowViewStateService.ViewState>
                            <scg:Dictionary x:TypeArguments="x:String, x:Object">
                              <x:Boolean x:Key="IsExpanded">True</x:Boolean>
                            </scg:Dictionary>
                          </sap:WorkflowViewStateService.ViewState>
                        </asa2:CreateAuxPoint>
                        <FlowStep.Next>
                          <FlowDecision x:Name="__ReferenceID2" Condition="[AP1.Y &gt; P1.Y]" DisplayName="范围端点地面高于原点?" sap:VirtualizedContainerService.HintSize="70,87">
                            <sap:WorkflowViewStateService.ViewState>
                              <scg:Dictionary x:TypeArguments="x:String, x:Object">
                                <x:Boolean x:Key="IsExpanded">True</x:Boolean>
                                <av:Point x:Key="ShapeLocation">265,186.5</av:Point>
                                <av:Size x:Key="ShapeSize">70,87</av:Size>
                                <av:PointCollection x:Key="TrueConnector">265,230 140,230 140,309</av:PointCollection>
                                <av:PointCollection x:Key="FalseConnector">335,230 460,230 460,309</av:PointCollection>
                              </scg:Dictionary>
                            </sap:WorkflowViewStateService.ViewState>
                            <FlowDecision.True>
                              <FlowStep x:Name="__ReferenceID3">
                                <sap:WorkflowViewStateService.ViewState>
                                  <scg:Dictionary x:TypeArguments="x:String, x:Object">
                                    <av:Point x:Key="ShapeLocation">40,309</av:Point>
                                    <av:Size x:Key="ShapeSize">200,22</av:Size>
                                    <av:PointCollection x:Key="ConnectorLocation">140,331 140,359</av:PointCollection>
                                  </scg:Dictionary>
                                </sap:WorkflowViewStateService.ViewState>
                                <asa2:CreatePoint AutoLinkCodes="{x:Null}" AutoLinkGeometryName="{x:Null}" ActivityId="3" ApplyAOR="False" AutoLink="False" DisplayName="P2 开挖起点(平底端)" FromPoint="P1" Geometry="[Geometry]" sap:VirtualizedContainerService.HintSize="200,22" PointNumber="P2" Positioning="DeltaXAndDeltaY" ShowErrors="True" Side="[Side]" SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]">
                                  <asa2:CreatePoint.Arguments>
                                    <InArgument x:TypeArguments="x:Double" x:Key="DeltaX1">[SearchOffset]</InArgument>
                                    <InArgument x:TypeArguments="x:Double" x:Key="DeltaY1">0</InArgument>
                                  </asa2:CreatePoint.Arguments>
                                  <asa2:CreatePoint.PointCodes>
                                    <scg:List x:TypeArguments="InArgument(x:String)" Capacity="4">
                                      <InArgument x:TypeArguments="x:String">DigStart</InArgument>
                                    </scg:List>
                                  </asa2:CreatePoint.PointCodes>
                                  <sap:WorkflowViewStateService.ViewState>
                                    <scg:Dictionary x:TypeArguments="x:String, x:Object">
                                      <x:Boolean x:Key="IsExpanded">True</x:Boolean>
                                    </scg:Dictionary>
                                  </sap:WorkflowViewStateService.ViewState>
                                </asa2:CreatePoint>
                                <FlowStep.Next>
                                  <FlowStep x:Name="__ReferenceID4">
                                    <sap:WorkflowViewStateService.ViewState>
                                      <scg:Dictionary x:TypeArguments="x:String, x:Object">
                                        <av:Point x:Key="ShapeLocation">40,359</av:Point>
                                        <av:Size x:Key="ShapeSize">200,22</av:Size>
                                        <av:PointCollection x:Key="ConnectorLocation">140,381 140,409</av:PointCollection>
                                      </scg:Dictionary>
                                    </sap:WorkflowViewStateService.ViewState>
                                    <asa2:CreateLink ActivityId="4" ApplyAOR="False" DisplayName="L1 平底开挖" EndPoint="P2" Geometry="[Geometry]" sap:VirtualizedContainerService.HintSize="200,22" IsEnabled="True" LinkNumber="L1" ShowErrors="True" StartPoint="P1" SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]">
                                      <asa2:CreateLink.LinkCodes>
                                        <scg:List x:TypeArguments="InArgument(x:String)" Capacity="4">
                                          <InArgument x:TypeArguments="x:String">Top</InArgument>
                                          <InArgument x:TypeArguments="x:String">Datum</InArgument>
                                          <InArgument x:TypeArguments="x:String">Flat</InArgument>
                                          <InArgument x:TypeArguments="x:String">Cut</InArgument>
                                        </scg:List>
                                      </asa2:CreateLink.LinkCodes>
                                      <sap:WorkflowViewStateService.ViewState>
                                        <scg:Dictionary x:TypeArguments="x:String, x:Object">
                                          <x:Boolean x:Key="IsExpanded">True</x:Boolean>
                                        </scg:Dictionary>
                                      </sap:WorkflowViewStateService.ViewState>
                                    </asa2:CreateLink>
                                    <FlowStep.Next>
                                      <FlowStep x:Name="__ReferenceID5">
                                        <sap:WorkflowViewStateService.ViewState>
                                          <scg:Dictionary x:TypeArguments="x:String, x:Object">
                                            <av:Point x:Key="ShapeLocation">40,409</av:Point>
                                            <av:Size x:Key="ShapeSize">200,22</av:Size>
                                            <av:PointCollection x:Key="ConnectorLocation">140,431 140,459</av:PointCollection>
                                          </scg:Dictionary>
                                        </sap:WorkflowViewStateService.ViewState>
                                        <asa2:CreatePoint AutoLinkCodes="{x:Null}" AutoLinkGeometryName="{x:Null}" ActivityId="5" ApplyAOR="False" AutoLink="False" DisplayName="P3 放坡出地面" FromPoint="P2" Geometry="[Geometry]" sap:VirtualizedContainerService.HintSize="200,22" PointNumber="P3" Positioning="SlopeToSurface" ShowErrors="True" Side="[Side]" SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]">
                                          <asa2:CreatePoint.Arguments>
                                            <InArgument x:TypeArguments="x:Double" x:Key="Slope3">[1.0 / SlopeH]</InArgument>
                                            <InArgument x:TypeArguments="x:Boolean" x:Key="ReverseSlopeDirection3">False</InArgument>
                                            <InArgument x:TypeArguments="asw:SurfaceTarget" x:Key="SurfaceTarget3">[EG_Surface]</InArgument>
                                            <InArgument x:TypeArguments="x:Double" x:Key="DeltaXForLayout3">5</InArgument>
                                          </asa2:CreatePoint.Arguments>
                                          <asa2:CreatePoint.PointCodes>
                                            <scg:List x:TypeArguments="InArgument(x:String)" Capacity="4">
                                              <InArgument x:TypeArguments="x:String">Daylight</InArgument>
                                              <InArgument x:TypeArguments="x:String">Daylight_Cut</InArgument>
                                            </scg:List>
                                          </asa2:CreatePoint.PointCodes>
                                          <sap:WorkflowViewStateService.ViewState>
                                            <scg:Dictionary x:TypeArguments="x:String, x:Object">
                                              <x:Boolean x:Key="IsExpanded">True</x:Boolean>
                                            </scg:Dictionary>
                                          </sap:WorkflowViewStateService.ViewState>
                                        </asa2:CreatePoint>
                                        <FlowStep.Next>
                                          <FlowStep x:Name="__ReferenceID6">
                                            <sap:WorkflowViewStateService.ViewState>
                                              <scg:Dictionary x:TypeArguments="x:String, x:Object">
                                                <av:Point x:Key="ShapeLocation">40,459</av:Point>
                                                <av:Size x:Key="ShapeSize">200,22</av:Size>
                                              </scg:Dictionary>
                                            </sap:WorkflowViewStateService.ViewState>
                                            <asa2:CreateLink ActivityId="6" ApplyAOR="False" DisplayName="L2 放坡链接" EndPoint="P3" Geometry="[Geometry]" sap:VirtualizedContainerService.HintSize="200,22" IsEnabled="True" LinkNumber="L2" ShowErrors="True" StartPoint="P2" SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]">
                                              <asa2:CreateLink.LinkCodes>
                                                <scg:List x:TypeArguments="InArgument(x:String)" Capacity="4">
                                                  <InArgument x:TypeArguments="x:String">Top</InArgument>
                                                  <InArgument x:TypeArguments="x:String">Datum</InArgument>
                                                  <InArgument x:TypeArguments="x:String">Daylight</InArgument>
                                                  <InArgument x:TypeArguments="x:String">Cut</InArgument>
                                                </scg:List>
                                              </asa2:CreateLink.LinkCodes>
                                              <sap:WorkflowViewStateService.ViewState>
                                                <scg:Dictionary x:TypeArguments="x:String, x:Object">
                                                  <x:Boolean x:Key="IsExpanded">True</x:Boolean>
                                                </scg:Dictionary>
                                              </sap:WorkflowViewStateService.ViewState>
                                            </asa2:CreateLink>
                                          </FlowStep>
                                        </FlowStep.Next>
                                      </FlowStep>
                                    </FlowStep.Next>
                                  </FlowStep>
                                </FlowStep.Next>
                              </FlowStep>
                            </FlowDecision.True>
                            <FlowDecision.False>
                              <FlowStep x:Name="__ReferenceID7">
                                <sap:WorkflowViewStateService.ViewState>
                                  <scg:Dictionary x:TypeArguments="x:String, x:Object">
                                    <av:Point x:Key="ShapeLocation">360,309</av:Point>
                                    <av:Size x:Key="ShapeSize">200,22</av:Size>
                                    <av:PointCollection x:Key="ConnectorLocation">460,331 460,356.5</av:PointCollection>
                                  </scg:Dictionary>
                                </sap:WorkflowViewStateService.ViewState>
                                <asa2:CreateAuxPoint AutoLinkCodes="{x:Null}" AutoLinkGeometryName="{x:Null}" PointCodes="{x:Null}" ActivityId="7" ApplyAOR="False" AutoLink="False" DisplayName="AP2 原点处探地面" FromPoint="P1" Geometry="[Geometry]" sap:VirtualizedContainerService.HintSize="200,22" PointNumber="AP2" Positioning="DeltaXOnSurface" ShowErrors="True" Side="[Side]" SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]">
                                  <asa2:CreateAuxPoint.Arguments>
                                    <InArgument x:TypeArguments="x:Double" x:Key="DeltaX4">0</InArgument>
                                    <InArgument x:TypeArguments="asw:SurfaceTarget" x:Key="SurfaceTarget4">[EG_Surface]</InArgument>
                                    <x:Null x:Key="OffsetTarget4" />
                                    <InArgument x:TypeArguments="x:Double" x:Key="DeltaYForLayout4">3</InArgument>
                                  </asa2:CreateAuxPoint.Arguments>
                                  <sap:WorkflowViewStateService.ViewState>
                                    <scg:Dictionary x:TypeArguments="x:String, x:Object">
                                      <x:Boolean x:Key="IsExpanded">True</x:Boolean>
                                    </scg:Dictionary>
                                  </sap:WorkflowViewStateService.ViewState>
                                </asa2:CreateAuxPoint>
                                <FlowStep.Next>
                                  <FlowDecision x:Name="__ReferenceID8" Condition="[AP2.Y &gt; P1.Y]" DisplayName="原点处地面高于原点?" sap:VirtualizedContainerService.HintSize="70,87">
                                    <sap:WorkflowViewStateService.ViewState>
                                      <scg:Dictionary x:TypeArguments="x:String, x:Object">
                                        <x:Boolean x:Key="IsExpanded">True</x:Boolean>
                                        <av:Point x:Key="ShapeLocation">425,356.5</av:Point>
                                        <av:Size x:Key="ShapeSize">70,87</av:Size>
                                        <av:PointCollection x:Key="TrueConnector">460,443.5 460,479</av:PointCollection>
                                      </scg:Dictionary>
                                    </sap:WorkflowViewStateService.ViewState>
                                    <FlowDecision.True>
                                      <FlowStep x:Name="__ReferenceID9">
                                        <sap:WorkflowViewStateService.ViewState>
                                          <scg:Dictionary x:TypeArguments="x:String, x:Object">
                                            <av:Point x:Key="ShapeLocation">360,479</av:Point>
                                            <av:Size x:Key="ShapeSize">200,22</av:Size>
                                            <av:PointCollection x:Key="ConnectorLocation">460,501 460,529</av:PointCollection>
                                          </scg:Dictionary>
                                        </sap:WorkflowViewStateService.ViewState>
                                        <asa2:CreatePoint AutoLinkCodes="{x:Null}" AutoLinkGeometryName="{x:Null}" ActivityId="8" ApplyAOR="False" AutoLink="False" DisplayName="P4 平坡收口交点" FromPoint="P1" Geometry="[Geometry]" sap:VirtualizedContainerService.HintSize="200,22" PointNumber="P4" Positioning="SlopeToSurface" ShowErrors="True" Side="[Side]" SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]">
                                          <asa2:CreatePoint.Arguments>
                                            <InArgument x:TypeArguments="x:Double" x:Key="Slope3">0</InArgument>
                                            <InArgument x:TypeArguments="x:Boolean" x:Key="ReverseSlopeDirection3">False</InArgument>
                                            <InArgument x:TypeArguments="asw:SurfaceTarget" x:Key="SurfaceTarget3">[EG_Surface]</InArgument>
                                            <InArgument x:TypeArguments="x:Double" x:Key="DeltaXForLayout3">[SearchOffset]</InArgument>
                                          </asa2:CreatePoint.Arguments>
                                          <asa2:CreatePoint.PointCodes>
                                            <scg:List x:TypeArguments="InArgument(x:String)" Capacity="4">
                                              <InArgument x:TypeArguments="x:String">Daylight</InArgument>
                                              <InArgument x:TypeArguments="x:String">Daylight_Flat</InArgument>
                                            </scg:List>
                                          </asa2:CreatePoint.PointCodes>
                                          <sap:WorkflowViewStateService.ViewState>
                                            <scg:Dictionary x:TypeArguments="x:String, x:Object">
                                              <x:Boolean x:Key="IsExpanded">True</x:Boolean>
                                            </scg:Dictionary>
                                          </sap:WorkflowViewStateService.ViewState>
                                        </asa2:CreatePoint>
                                        <FlowStep.Next>
                                          <FlowStep x:Name="__ReferenceID10">
                                            <sap:WorkflowViewStateService.ViewState>
                                              <scg:Dictionary x:TypeArguments="x:String, x:Object">
                                                <av:Point x:Key="ShapeLocation">360,529</av:Point>
                                                <av:Size x:Key="ShapeSize">200,22</av:Size>
                                              </scg:Dictionary>
                                            </sap:WorkflowViewStateService.ViewState>
                                            <asa2:CreateLink ActivityId="9" ApplyAOR="False" DisplayName="L3 平坡链接" EndPoint="P4" Geometry="[Geometry]" sap:VirtualizedContainerService.HintSize="200,22" IsEnabled="True" LinkNumber="L3" ShowErrors="True" StartPoint="P1" SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]">
                                              <asa2:CreateLink.LinkCodes>
                                                <scg:List x:TypeArguments="InArgument(x:String)" Capacity="4">
                                                  <InArgument x:TypeArguments="x:String">Top</InArgument>
                                                  <InArgument x:TypeArguments="x:String">Datum</InArgument>
                                                  <InArgument x:TypeArguments="x:String">Daylight</InArgument>
                                                  <InArgument x:TypeArguments="x:String">Flat</InArgument>
                                                </scg:List>
                                              </asa2:CreateLink.LinkCodes>
                                              <sap:WorkflowViewStateService.ViewState>
                                                <scg:Dictionary x:TypeArguments="x:String, x:Object">
                                                  <x:Boolean x:Key="IsExpanded">True</x:Boolean>
                                                </scg:Dictionary>
                                              </sap:WorkflowViewStateService.ViewState>
                                            </asa2:CreateLink>
                                          </FlowStep>
                                        </FlowStep.Next>
                                      </FlowStep>
                                    </FlowDecision.True>
                                  </FlowDecision>
                                </FlowStep.Next>
                              </FlowStep>
                            </FlowDecision.False>
                          </FlowDecision>
                        </FlowStep.Next>
                      </FlowStep>
                    </FlowStep.Next>
                  </FlowStep>
                </Flowchart.StartNode>
                <x:Reference>__ReferenceID0</x:Reference>
                <x:Reference>__ReferenceID1</x:Reference>
                <x:Reference>__ReferenceID2</x:Reference>
                <x:Reference>__ReferenceID3</x:Reference>
                <x:Reference>__ReferenceID4</x:Reference>
                <x:Reference>__ReferenceID5</x:Reference>
                <x:Reference>__ReferenceID6</x:Reference>
                <x:Reference>__ReferenceID7</x:Reference>
                <x:Reference>__ReferenceID8</x:Reference>
                <x:Reference>__ReferenceID9</x:Reference>
                <x:Reference>__ReferenceID10</x:Reference>
              </Flowchart>
            </Activity>
            """;
    }
}
