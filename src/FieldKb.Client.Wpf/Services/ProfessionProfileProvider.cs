namespace FieldKb.Client.Wpf;

public sealed class ProfessionProfileProvider : IProfessionProfileProvider
{
    private static readonly IReadOnlyDictionary<string, ProfessionProfile> Profiles = BuildProfiles();

    public ProfessionProfile GetProfile(string professionId)
    {
        var id = ProfessionIds.Normalize(professionId);
        return Profiles.TryGetValue(id, out var p) ? p : Profiles[ProfessionIds.General];
    }

    private static IReadOnlyDictionary<string, ProfessionProfile> BuildProfiles()
    {
        var common = new[]
        {
            "站点", "项目", "产线", "设备编号", "应用版本", "操作系统", "网络", "备注",
            "复现步骤", "日志关键字", "构建号", "用例编号"
        };

        var general = new ProfessionProfile(
            Id: ProfessionIds.General,
            DisplayName: "通用（默认）",
            FixedFields: new[]
            {
                new FixedFieldDefinition("deviceModel", "设备型号", FixedFieldValidation.None),
                new FixedFieldDefinition("deviceVersion", "设备版本", FixedFieldValidation.None),
                new FixedFieldDefinition("workstation", "工位/线体", FixedFieldValidation.None),
                new FixedFieldDefinition("customer", "客户", FixedFieldValidation.None),
                new FixedFieldDefinition("ip", "IP 地址", FixedFieldValidation.IpAddress),
                new FixedFieldDefinition("port", "端口", FixedFieldValidation.Port)
            },
            CommonKeys: common,
            DefaultCustomEntries: new[]
            {
                new EnvironmentEntry("项目", string.Empty),
                new EnvironmentEntry("应用版本", string.Empty),
                new EnvironmentEntry("备注", string.Empty)
            });

        var hardware = new ProfessionProfile(
            Id: ProfessionIds.Hardware,
            DisplayName: "硬件工程师",
            FixedFields: new[]
            {
                new FixedFieldDefinition("deviceModel", "设备型号", FixedFieldValidation.None),
                new FixedFieldDefinition("deviceVersion", "设备版本", FixedFieldValidation.None),
                new FixedFieldDefinition("workstation", "工位/线体", FixedFieldValidation.None),
                new FixedFieldDefinition("customer", "客户", FixedFieldValidation.None),
                new FixedFieldDefinition("powerSupplyModel", "电源型号", FixedFieldValidation.None),
                new FixedFieldDefinition("powerSupplyVersion", "电源版本", FixedFieldValidation.None),
                new FixedFieldDefinition("ip", "IP 地址", FixedFieldValidation.IpAddress),
                new FixedFieldDefinition("port", "端口", FixedFieldValidation.Port)
            },
            CommonKeys: common.Concat(new[]
            {
                "供电电压", "接地", "EMI/ESD", "继电器型号", "传感器型号", "线缆类型", "安装环境"
            }).Distinct(StringComparer.Ordinal).ToArray(),
            DefaultCustomEntries: new[]
            {
                new EnvironmentEntry("设备编号", string.Empty),
                new EnvironmentEntry("供电电压", string.Empty),
                new EnvironmentEntry("接地", string.Empty),
                new EnvironmentEntry("备注", string.Empty)
            });

        var software = new ProfessionProfile(
            Id: ProfessionIds.Software,
            DisplayName: "软件工程师",
            FixedFields: new[]
            {
                new FixedFieldDefinition("appName", "应用名称", FixedFieldValidation.None),
                new FixedFieldDefinition("appVersion", "应用版本", FixedFieldValidation.None),
                new FixedFieldDefinition("os", "操作系统", FixedFieldValidation.None),
                new FixedFieldDefinition("runtime", "运行时", FixedFieldValidation.None),
                new FixedFieldDefinition("deployEnv", "环境", FixedFieldValidation.None),
                new FixedFieldDefinition("customer", "客户", FixedFieldValidation.None),
                new FixedFieldDefinition("traceId", "TraceId", FixedFieldValidation.None),
                new FixedFieldDefinition("userAccount", "用户账号", FixedFieldValidation.None)
            },
            CommonKeys: common.Concat(new[]
            {
                "代码分支", "构建号", "配置版本", "依赖版本", "数据库版本", "API 地址"
            }).Distinct(StringComparer.Ordinal).ToArray(),
            DefaultCustomEntries: new[]
            {
                new EnvironmentEntry("复现步骤", string.Empty),
                new EnvironmentEntry("日志关键字", string.Empty),
                new EnvironmentEntry("备注", string.Empty)
            });

        var embedded = new ProfessionProfile(
            Id: ProfessionIds.Embedded,
            DisplayName: "嵌入式工程师",
            FixedFields: new[]
            {
                new FixedFieldDefinition("deviceModel", "设备型号", FixedFieldValidation.None),
                new FixedFieldDefinition("firmwareVersion", "固件版本", FixedFieldValidation.None),
                new FixedFieldDefinition("bootloaderVersion", "Bootloader 版本", FixedFieldValidation.None),
                new FixedFieldDefinition("socModel", "SoC/MCU 型号", FixedFieldValidation.None),
                new FixedFieldDefinition("toolchain", "工具链/编译器", FixedFieldValidation.None),
                new FixedFieldDefinition("rtos", "RTOS/OS", FixedFieldValidation.None),
                new FixedFieldDefinition("ip", "IP 地址", FixedFieldValidation.IpAddress),
                new FixedFieldDefinition("port", "端口", FixedFieldValidation.Port)
            },
            CommonKeys: common.Concat(new[]
            {
                "板卡版本", "外设型号", "串口波特率", "升级方式", "日志片段", "看门狗", "内存/Flash"
            }).Distinct(StringComparer.Ordinal).ToArray(),
            DefaultCustomEntries: new[]
            {
                new EnvironmentEntry("板卡版本", string.Empty),
                new EnvironmentEntry("升级方式", string.Empty),
                new EnvironmentEntry("日志片段", string.Empty)
            });

        var ui = new ProfessionProfile(
            Id: ProfessionIds.Ui,
            DisplayName: "UI 工程师",
            FixedFields: new[]
            {
                new FixedFieldDefinition("clientApp", "客户端应用", FixedFieldValidation.None),
                new FixedFieldDefinition("clientVersion", "客户端版本", FixedFieldValidation.None),
                new FixedFieldDefinition("os", "操作系统", FixedFieldValidation.None),
                new FixedFieldDefinition("resolution", "分辨率", FixedFieldValidation.None),
                new FixedFieldDefinition("dpiScale", "DPI/缩放", FixedFieldValidation.None),
                new FixedFieldDefinition("theme", "主题/配色", FixedFieldValidation.None),
                new FixedFieldDefinition("locale", "语言/区域", FixedFieldValidation.None),
                new FixedFieldDefinition("gpuDriver", "显卡/驱动", FixedFieldValidation.None)
            },
            CommonKeys: common.Concat(new[]
            {
                "UI 入口路径", "截图/录屏", "交互说明", "崩溃堆栈", "性能指标"
            }).Distinct(StringComparer.Ordinal).ToArray(),
            DefaultCustomEntries: new[]
            {
                new EnvironmentEntry("UI 入口路径", string.Empty),
                new EnvironmentEntry("复现步骤", string.Empty),
                new EnvironmentEntry("备注", string.Empty)
            });

        var qa = new ProfessionProfile(
            Id: ProfessionIds.Qa,
            DisplayName: "测试/QA",
            FixedFields: new[]
            {
                new FixedFieldDefinition("appName", "应用名称", FixedFieldValidation.None),
                new FixedFieldDefinition("appVersion", "应用版本", FixedFieldValidation.None),
                new FixedFieldDefinition("testEnv", "测试环境", FixedFieldValidation.None),
                new FixedFieldDefinition("buildNo", "构建号", FixedFieldValidation.None),
                new FixedFieldDefinition("deviceModel", "设备型号", FixedFieldValidation.None),
                new FixedFieldDefinition("os", "操作系统", FixedFieldValidation.None),
                new FixedFieldDefinition("severityHint", "严重级别（建议）", FixedFieldValidation.None),
                new FixedFieldDefinition("reproducibility", "复现概率", FixedFieldValidation.None)
            },
            CommonKeys: common.Concat(new[]
            {
                "前置条件", "测试数据", "期望结果", "实际结果"
            }).Distinct(StringComparer.Ordinal).ToArray(),
            DefaultCustomEntries: new[]
            {
                new EnvironmentEntry("用例编号", string.Empty),
                new EnvironmentEntry("复现步骤", string.Empty),
                new EnvironmentEntry("期望结果", string.Empty),
                new EnvironmentEntry("实际结果", string.Empty)
            });

        var ops = new ProfessionProfile(
            Id: ProfessionIds.Ops,
            DisplayName: "运维/现场实施",
            FixedFields: new[]
            {
                new FixedFieldDefinition("site", "站点", FixedFieldValidation.None),
                new FixedFieldDefinition("customer", "客户", FixedFieldValidation.None),
                new FixedFieldDefinition("systemName", "系统/项目", FixedFieldValidation.None),
                new FixedFieldDefinition("deployEnv", "环境", FixedFieldValidation.None),
                new FixedFieldDefinition("hostName", "主机名", FixedFieldValidation.None),
                new FixedFieldDefinition("ip", "IP 地址", FixedFieldValidation.IpAddress),
                new FixedFieldDefinition("port", "端口", FixedFieldValidation.Port),
                new FixedFieldDefinition("versionCombo", "版本组合", FixedFieldValidation.None)
            },
            CommonKeys: common.Concat(new[]
            {
                "变更单号", "告警编号", "时间范围", "服务名", "数据库连接", "回滚方案"
            }).Distinct(StringComparer.Ordinal).ToArray(),
            DefaultCustomEntries: new[]
            {
                new EnvironmentEntry("变更单号", string.Empty),
                new EnvironmentEntry("告警编号", string.Empty),
                new EnvironmentEntry("日志关键字", string.Empty),
                new EnvironmentEntry("备注", string.Empty)
            });

        return new Dictionary<string, ProfessionProfile>(StringComparer.Ordinal)
        {
            [general.Id] = general,
            [hardware.Id] = hardware,
            [software.Id] = software,
            [embedded.Id] = embedded,
            [ui.Id] = ui,
            [qa.Id] = qa,
            [ops.Id] = ops
        };
    }
}

