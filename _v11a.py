# -*- coding: utf-8 -*-
"""v1.1.0: 游戏扫描扩容 + 优化项分组 + 配置方案 Profiles"""
import io, json, re

BS = chr(92)
def load(p): return io.open(p, encoding='utf-8-sig').read()
def save(p, s): io.open(p, 'w', encoding='utf-8-sig').write(s.replace('\r\n', '\n'))

# ================= A. 游戏扫描扩容 =================
p = 'FpsTune.Wpf/Core/GamePathService.cs'
s = load(p)
old = '''        "cs2", "valve_w64", "VALORANT-Win64-Shipping", "Apex", "r5apex_dx12",
        "TslGame", "cod", "cod22-cod", "Overwatch", "TheFinals",
        "DeltaForceClient-Win64-Shipping", "DeltaForceClient", "DeltaForce"'''
assert old in s
s = s.replace(old, '''        "cs2", "valve_w64", "VALORANT-Win64-Shipping", "Apex", "r5apex_dx12",
        "TslGame", "cod", "cod22-cod", "Overwatch", "TheFinals",
        "RainbowSix", "EscapeFromTarkov", "destiny2", "BF2042",
        "DeltaForceClient-Win64-Shipping", "DeltaForceClient", "DeltaForce"''')
old2 = '''        "cs2.exe", "VALORANT-Win64-Shipping.exe", "r5apex_dx12.exe",
        "TslGame.exe", "Overwatch.exe", "cod.exe", "TheFinals.exe",
        "DeltaForceClient-Win64-Shipping.exe", "DeltaForceClient.exe"'''
assert old2 in s
s = s.replace(old2, '''        "cs2.exe", "VALORANT-Win64-Shipping.exe", "r5apex_dx12.exe",
        "TslGame.exe", "Overwatch.exe", "cod.exe", "TheFinals.exe",
        "RainbowSix.exe", "EscapeFromTarkov.exe", "destiny2.exe", "BF2042.exe",
        "DeltaForceClient-Win64-Shipping.exe", "DeltaForceClient.exe"''')
old3 = '''               text.Contains("Overwatch", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("守望先锋", StringComparison.Ordinal) ||
               text.Contains("THE FINALS", StringComparison.OrdinalIgnoreCase);'''
assert old3 in s
s = s.replace(old3, '''               text.Contains("Overwatch", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("守望先锋", StringComparison.Ordinal) ||
               text.Contains("THE FINALS", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("彩虹六号", StringComparison.Ordinal) ||
               text.Contains("Rainbow Six", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("逃离塔科夫", StringComparison.Ordinal) ||
               text.Contains("Escape from Tarkov", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("命运2", StringComparison.Ordinal) ||
               text.Contains("Destiny 2", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("战地", StringComparison.Ordinal) ||
               text.Contains("Battlefield", StringComparison.OrdinalIgnoreCase);''')
save(p, s); print('[A1] C# 游戏扩容 ok')

p = 'fps-tune.ps1'
s = load(p)
old4 = """    $procNames = @('cs2', 'VALORANT-Win64-Shipping', 'r5apex_dx12', 'TslGame',
                   'Overwatch', 'cod', 'TheFinals',
                   'DeltaForceClient-Win64-Shipping', 'DeltaForceClient', 'DeltaForce')"""
assert old4 in s
s = s.replace(old4, """    $procNames = @('cs2', 'VALORANT-Win64-Shipping', 'r5apex_dx12', 'TslGame',
                   'Overwatch', 'cod', 'TheFinals', 'RainbowSix', 'EscapeFromTarkov',
                   'destiny2', 'BF2042',
                   'DeltaForceClient-Win64-Shipping', 'DeltaForceClient', 'DeltaForce')""")
old5 = "$dn -match '三角洲|Delta Force|DeltaForce|Counter-Strike|CS2|CS 2|VALORANT|Apex Legends|PUBG|绝地求生|Call of Duty|使命召唤|Overwatch|守望先锋|THE FINALS'"
assert old5 in s
s = s.replace(old5, "$dn -match '三角洲|Delta Force|DeltaForce|Counter-Strike|CS2|CS 2|VALORANT|Apex Legends|PUBG|绝地求生|Call of Duty|使命召唤|Overwatch|守望先锋|THE FINALS|彩虹六号|Rainbow Six|逃离塔科夫|Escape from Tarkov|命运2|Destiny 2|战地|Battlefield'")
old6 = "$exeNames = @('DeltaForceClient-Win64-Shipping.exe','cs2.exe','VALORANT-Win64-Shipping.exe','r5apex_dx12.exe','TslGame.exe','Overwatch.exe','cod.exe','TheFinals.exe')"
assert old6 in s
s = s.replace(old6, "$exeNames = @('DeltaForceClient-Win64-Shipping.exe','cs2.exe','VALORANT-Win64-Shipping.exe','r5apex_dx12.exe','TslGame.exe','Overwatch.exe','cod.exe','TheFinals.exe','RainbowSix.exe','EscapeFromTarkov.exe','destiny2.exe','BF2042.exe')")
save(p, s); print('[A2] PS 游戏扩容 ok')

# ================= B. 分组：catalog 重排 + group 字段 =================
GROUP_ORDER = ['键鼠', '图形显示', '网络', '电源', '系统与调度']
GROUP_MAP = {
    'keyboard-latency': '键鼠', 'keyboard-repeat': '键鼠', 'sticky-keys-off': '键鼠', 'mouse-accel-off': '键鼠',
    'hags': '图形显示', 'mpo-off': '图形显示', 'transparency-off': '图形显示',
    'fso-off': '图形显示', 'gpu-pref': '图形显示', 'gpu-pstate-lock': '图形显示',
    'net-throttling-off': '网络', 'net-nagle-off': '网络',
    'power-ultimate': '电源', 'power-tuning': '电源', 'usb-power-save-off': '电源',
    'game-mode': '系统与调度', 'dvr-off': '系统与调度', 'prio-separation': '系统与调度',
    'wer-off': '系统与调度', 'sys-responsiveness': '系统与调度', 'mmcss-games': '系统与调度',
    'sysmain-off': '系统与调度', 'wsearch-off': '系统与调度', 'hibernate-off': '系统与调度',
    'game-priority': '系统与调度', 'paging-exec': '系统与调度', 'mem-compress-off': '系统与调度',
    'dyntick-off': '系统与调度', 'menu-delay-off': '系统与调度',
}
p = 'catalog/catalog.json'
d = json.load(io.open(p, encoding='utf-8'))
assert len(d['items']) == 29
assert set(GROUP_MAP) == {i['id'] for i in d['items']}, '分组映射与 catalog 不一致'
new_items = []
for g in GROUP_ORDER:
    for it in d['items']:
        if GROUP_MAP[it['id']] == g:
            it['group'] = g
            new_items.append(it)
d['items'] = new_items
io.open(p, 'w', encoding='utf-8').write(json.dumps(d, ensure_ascii=False, indent=2) + '\n')
print('[B1] catalog 重排+分组 ok')

# C# record 增加 Group
p = 'FpsTune.Wpf/Core/OptimizationItemDefinition.cs'
s = load(p)
old = '''public sealed record OptimizationItemDefinition(
    string Id,
    string Name,
    string Description,
    string SideEffect,
    bool Admin,
    bool Reboot,
    string Kind);'''
assert old in s
s = s.replace(old, '''public sealed record OptimizationItemDefinition(
    string Id,
    string Name,
    string Description,
    string SideEffect,
    bool Admin,
    bool Reboot,
    string Kind,
    string Group);''')
save(p, s)

# AppState.OptimizationItem 增加 Group
p = 'FpsTune.Wpf/Services/AppState.cs'
s = load(p)
old = '''public sealed record OptimizationItem(
    string Id,
    string Name,
    string Description,
    string SideEffect,
    bool RequiresAdmin,
    bool RequiresReboot,
    bool Optimized,
    string Current,
    bool IsDefault);'''
assert old in s
s = s.replace(old, '''public sealed record OptimizationItem(
    string Id,
    string Name,
    string Description,
    string SideEffect,
    bool RequiresAdmin,
    bool RequiresReboot,
    bool Optimized,
    string Current,
    bool IsDefault,
    string Group);''')
save(p, s); print('[B2] C# 记录 ok')

# DetectionService: 检测载荷带 group
p = 'FpsTune.Wpf/Core/DetectionService.cs'
s = load(p)
old = '''                optimized = state.Optimized,
                current = state.Current
            };'''
assert old in s
s = s.replace(old, '''                optimized = state.Optimized,
                current = state.Current,
                group = def.Group
            };''')
save(p, s); print('[B3] 检测载荷 ok')

# DetectView: 解析 group
p = 'FpsTune.Wpf/Views/DetectView.xaml.cs'
s = load(p)
old = '''                var isDefault = item?["default"]?.GetValue<bool>() ?? false;
                AppState.Items.Add(new OptimizationItem(id, name, desc, sideEffect, admin, reboot, optimized, current, isDefault));'''
assert old in s
s = s.replace(old, '''                var isDefault = item?["default"]?.GetValue<bool>() ?? false;
                var group = item?["group"]?.GetValue<string>() ?? "";
                AppState.Items.Add(new OptimizationItem(id, name, desc, sideEffect, admin, reboot, optimized, current, isDefault, group));''')
save(p, s); print('[B4] 检测页解析 ok')

# VM 暴露 Group
p = 'FpsTune.Wpf/Services/OptimizationItemViewModel.cs'
s = load(p)
old = '''    public bool Optimized { get; }
    public string Current { get; }'''
assert old in s
s = s.replace(old, '''    public bool Optimized { get; }
    public string Current { get; }
    public string Group { get; }''')
old2 = '''        Optimized = item.Optimized;
        Current = item.Current;'''
assert old2 in s
s = s.replace(old2, '''        Optimized = item.Optimized;
        Current = item.Current;
        Group = item.Group;''')
save(p, s); print('[B5] VM ok')

# PS: 合并注入 group + 检测载荷带 group
p = 'fps-tune.ps1'
s = load(p)
old7 = "    $item.kind       = $meta.kind"
assert old7 in s
s = s.replace(old7, old7 + "\n    $item.group      = $meta.group")
old8 = "            optimized = $state.optimized; current = $state.current\n        }"
assert old8 in s
s = s.replace(old8, "            optimized = $state.optimized; current = $state.current; group = $it.group\n        }")
save(p, s); print('[B6] PS 分组 ok')

# ================= C. OptimizeView：分组 UI + 分类 chips + Profiles =================
p = 'FpsTune.Wpf/Views/OptimizeView.xaml'
s = load(p)

# C1: 分类 chips 行（插在列表卡头与列表之间）
old_c1 = '''                        <Button Content="清空"
                                Style="{StaticResource GhostButtonStyle}"
                                MinWidth="60" Height="30" Margin="6,0,0,0"
                                Click="ClearAll_Click"/>
                    </StackPanel>
                </Grid>
'''
assert old_c1 in s
chips = '''                        <Button Content="清空"
                                Style="{StaticResource GhostButtonStyle}"
                                MinWidth="60" Height="30" Margin="6,0,0,0"
                                Click="ClearAll_Click"/>
                    </StackPanel>
                </Grid>

                <!-- 分类过滤 -->
                <StackPanel Orientation="Horizontal" Margin="0,2,0,10">
                    <RadioButton Content="全部" GroupName="grpfilter" IsChecked="True" Tag=""
                                 Style="{StaticResource PillRadioStyle}" Checked="GroupChip_Checked"/>
                    <RadioButton Content="键鼠" GroupName="grpfilter" Tag="键鼠"
                                 Style="{StaticResource PillRadioStyle}" Checked="GroupChip_Checked"/>
                    <RadioButton Content="图形显示" GroupName="grpfilter" Tag="图形显示"
                                 Style="{StaticResource PillRadioStyle}" Checked="GroupChip_Checked"/>
                    <RadioButton Content="网络" GroupName="grpfilter" Tag="网络"
                                 Style="{StaticResource PillRadioStyle}" Checked="GroupChip_Checked"/>
                    <RadioButton Content="电源" GroupName="grpfilter" Tag="电源"
                                 Style="{StaticResource PillRadioStyle}" Checked="GroupChip_Checked"/>
                    <RadioButton Content="系统与调度" GroupName="grpfilter" Tag="系统与调度"
                                 Style="{StaticResource PillRadioStyle}" Checked="GroupChip_Checked"/>
                </StackPanel>
'''
s = s.replace(old_c1, chips)

# C2: ListBox 分组头
old_c2 = '''                             ItemsSource="{Binding ItemsView, RelativeSource={RelativeSource AncestorType=UserControl}}">
                        <ListBox.ItemTemplate>'''
assert old_c2 in s
s = s.replace(old_c2, '''                             ItemsSource="{Binding ItemsView, RelativeSource={RelativeSource AncestorType=UserControl}}">
                        <ListBox.GroupStyle>
                            <GroupStyle>
                                <GroupStyle.HeaderTemplate>
                                    <DataTemplate>
                                        <TextBlock Text="{Binding Name}"
                                                   Style="{StaticResource SectionLabelStyle}"
                                                   Margin="0,10,0,4"/>
                                    </DataTemplate>
                                </GroupStyle.HeaderTemplate>
                            </GroupStyle>
                        </ListBox.GroupStyle>
                        <ListBox.ItemTemplate>''')

# C3: 预设卡追加配置方案行
old_c3 = '''                <CheckBox x:Name="ConsentCheck"
                          Style="{StaticResource ToggleSwitchStyle}"
                          Content="我已了解各项作用，同意执行（写前自动备份，可还原）"
                          Margin="0,16,0,0"/>
            </StackPanel>'''
assert old_c3 in s
s = s.replace(old_c3, '''                <CheckBox x:Name="ConsentCheck"
                          Style="{StaticResource ToggleSwitchStyle}"
                          Content="我已了解各项作用，同意执行（写前自动备份，可还原）"
                          Margin="0,16,0,0"/>

                <TextBlock Text="配置方案" Style="{StaticResource MutedTextStyle}" Margin="0,18,0,0"/>
                <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
                    <TextBox x:Name="ProfileNameBox"
                             Style="{StaticResource InputTextBoxStyle}"
                             Width="200" Height="30"
                             VerticalContentAlignment="Center"
                             FontSize="12"/>
                    <Button Content="保存" Style="{StaticResource GhostButtonStyle}" MinWidth="56" Height="30" Margin="8,0,0,0" Click="ProfileSave_Click"/>
                    <Button Content="载入" Style="{StaticResource GhostButtonStyle}" MinWidth="56" Height="30" Margin="6,0,0,0" Click="ProfileLoad_Click"/>
                    <Button Content="删除" Style="{StaticResource GhostButtonStyle}" MinWidth="56" Height="30" Margin="6,0,0,0" Click="ProfileDelete_Click"/>
                    <Button Content="导出" Style="{StaticResource GhostButtonStyle}" MinWidth="56" Height="30" Margin="6,0,0,0" Click="ProfileExport_Click"/>
                    <Button Content="导入" Style="{StaticResource GhostButtonStyle}" MinWidth="56" Height="30" Margin="6,0,0,0" Click="ProfileImport_Click"/>
                </StackPanel>
                <TextBlock x:Name="ProfileHint"
                           Text="输入名称后保存当前勾选；载入按名称应用；导出/导入用于分享"
                           Style="{StaticResource MutedTextStyle}" Margin="0,8,0,0"/>
            </StackPanel>''')
save(p, s); print('[C] OptimizeView XAML ok')

# StateStore: Profiles 持久化
p = 'FpsTune.Wpf/Services/StateStore.cs'
s = load(p)
s = s.replace('using System.Text.Json.Nodes;',
              'using System.Text.Json;\nusing System.Text.Json.Nodes;')
add = '''
public sealed record OptProfile(string Name, IReadOnlyList<string> Ids);

public static class ProfileStore
{
    private static string ProfilesFile => Path.Combine(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FpsTune"),
        "profiles.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static List<OptProfile> Load()
    {
        try
        {
            if (!File.Exists(ProfilesFile))
                return new List<OptProfile>();
            return JsonSerializer.Deserialize<List<OptProfile>>(File.ReadAllText(ProfilesFile, Encoding.UTF8))
                   ?? new List<OptProfile>();
        }
        catch
        {
            return new List<OptProfile>();
        }
    }

    public static void Save(List<OptProfile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProfilesFile)!);
        File.WriteAllText(ProfilesFile, JsonSerializer.Serialize(profiles, JsonOpts), new UTF8Encoding(false));
    }
}
'''
s = s.rstrip() + '\n' + add
save(p, s); print('[D] ProfileStore ok')
