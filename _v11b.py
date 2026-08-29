# -*- coding: utf-8 -*-
"""v1.1.0 part B: OptimizeView 分组/分类/方案 UI + 代码后台 + 测试"""
import io, re

BS = chr(92)
def load(p): return io.open(p, encoding='utf-8-sig').read()
def save(p, s): io.open(p, 'w', encoding='utf-8-sig').write(s.replace('\r\n', '\n'))

# ============ 1. OptimizeView.xaml ============
p = 'FpsTune.Wpf/Views/OptimizeView.xaml'
s = load(p)

# 1a. 分类 chips（插在列表卡头部 Grid 之后）
old_c1 = '''Click="ClearAll_Click"/>
                    </StackPanel>
                </Grid>
'''
assert s.count(old_c1) == 1, 'c1 anchor'
chips = '''Click="ClearAll_Click"/>
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

# 1b. ListBox 分组头
old_c2 = '''                         ItemsSource="{Binding ItemsView, RelativeSource={RelativeSource AncestorType=UserControl}}">
                    <ListBox.ItemTemplate>'''
assert s.count(old_c2) == 1, 'c2 anchor'
s = s.replace(old_c2, '''                         ItemsSource="{Binding ItemsView, RelativeSource={RelativeSource AncestorType=UserControl}}">
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

# 1c. 配置方案卡：包一层 StackPanel，插在预设卡之后
old_c3 = '''        <!-- 优化项列表 -->
        <Border Grid.Row="2" Style="{StaticResource CardStyle}" Margin="0,0,0,14">'''
assert s.count(old_c3) == 1, 'c3 anchor'
profiles_card = '''        <Border Grid.Row="1" Style="{StaticResource CardStyle}" Margin="0,0,0,14">
            <StackPanel>
                <TextBlock Text="配置方案" Style="{StaticResource CardTitleStyle}"/>
                <StackPanel Orientation="Horizontal">
                    <TextBox x:Name="ProfileNameBox"
                             Style="{StaticResource InputTextBoxStyle}"
                             Width="220" Height="30"
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
            </StackPanel>
        </Border>

        <!-- 优化项列表 -->
        <Border Grid.Row="2" Style="{StaticResource CardStyle}" Margin="0,0,0,14">'''
# 预设卡的 Border 需要 Grid.Row=1 且被容器包裹：先给预设 Border 包 StackPanel
old_preset_open = '''        <Border Grid.Row="1" Style="{StaticResource CardStyle}" Margin="0,0,0,14">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="24"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>'''
assert s.count(old_preset_open) == 1, 'preset open'
s = s.replace(old_preset_open, '''        <StackPanel Grid.Row="1" Margin="0,0,0,14">
        <Border Style="{StaticResource CardStyle}">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="24"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>''')

# 预设卡结束：找到 </Grid>\n        </Border>\n(优化项列表注释前)
old_preset_close = '''            </Grid>
        </Border>

        <!-- 优化项列表 -->'''
assert s.count(old_preset_close) == 1, 'preset close'
s = s.replace(old_preset_close, '''            </Grid>
        </Border>

''' + profiles_card + '''
        </StackPanel>

        <!-- 优化项列表 -->''')
save(p, s); print('[XAML] ok')

# ============ 2. OptimizeView.xaml.cs ============
p = 'FpsTune.Wpf/Views/OptimizeView.xaml.cs'
s = load(p)

# usings
s = s.replace('using System.Collections.ObjectModel;',
              'using System.Collections.ObjectModel;\nusing System.ComponentModel;')
if 'using System.IO;' not in s:
    s = s.replace('using System.Text.Json.Nodes;', 'using System.IO;\nusing System.Text.Json;\nusing System.Text.Json.Nodes;')

# ctor: 分组描述
old_ctor = '        ItemsView.Filter = FilterItem;'
assert old_ctor in s
s = s.replace(old_ctor, '''        ItemsView.Filter = FilterItem;
        ItemsView.GroupDescriptions.Add(
            new System.Windows.Data.PropertyGroupDescription(nameof(OptimizationItemViewModel.Group)));''')

# FilterItem: 组过滤
old_f = '''        if (_searchText.Length == 0)
            return true;
        return vm.Id.Contains(_searchText, StringComparison.OrdinalIgnoreCase)'''
assert old_f in s
s = s.replace(old_f, '''        if (_groupFilter.Length > 0 && vm.Group != _groupFilter)
            return false;
        if (_searchText.Length == 0)
            return true;
        return vm.Id.Contains(_searchText, StringComparison.OrdinalIgnoreCase)''')

# 字段
old_field = '    private string _searchText = "";'
assert old_field in s
s = s.replace(old_field, '''    private string _searchText = "";
    private string _groupFilter = "";''')

# GroupChip handler（插在 SearchBox_TextChanged 前）
anchor_h = '    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)'
assert anchor_h in s
handler = '''    private void GroupChip_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb && rb.Tag is string g)
        {
            _groupFilter = g;
            ItemsView.Refresh();
        }
    }

'''
s = s.replace(anchor_h, handler + anchor_h)

# Profiles 方法（追加到类尾：倒数第二个 '}' 前）
tail = s.rstrip()
assert tail.endswith('}'), 'file tail'
idx_ns = tail.rfind('\n}')
idx_cls = tail.rfind('\n}', 0, idx_ns)
methods = '''
    // ---------- 配置方案 ----------

    private string CurrentProfileName => ProfileNameBox.Text.Trim();

    private void SetProfileHint(string text)
    {
        ProfileHint.Text = text;
    }

    private void ApplyProfileIds(IEnumerable<string> ids)
    {
        _suppressPresetAutoCheck = true;
        PresetCustom.IsChecked = true;
        _suppressPresetAutoCheck = false;

        var idSet = ids.ToHashSet();
        foreach (var vm in Items)
            vm.IsChecked = idSet.Contains(vm.Id);

        var sb = new StringBuilder();
        sb.AppendLine($"== 方案已载入（{idSet.Count} 项） ==");
        foreach (var vm in Items.Where(v => v.IsChecked))
            sb.AppendLine($"{vm.Id}  {vm.Name}");
        OutputBox.Text = sb.ToString();
    }

    private void ProfileSave_Click(object sender, RoutedEventArgs e)
    {
        var name = CurrentProfileName;
        if (name.Length == 0)
        {
            DialogService.Warning("配置方案", "请先在输入框填写方案名称。");
            return;
        }
        var ids = Items.Where(i => i.IsChecked).Select(i => i.Id).ToList();
        if (ids.Count == 0)
        {
            DialogService.Warning("配置方案", "当前没有勾选任何优化项。");
            return;
        }

        var profiles = ProfileStore.Load();
        var existing = profiles.FirstOrDefault(p => p.Name == name);
        if (existing is not null
            && !DialogService.Confirm("配置方案", $"方案「{name}」已存在，覆盖？", danger: true))
            return;
        profiles.RemoveAll(p => p.Name == name);
        profiles.Add(new OptProfile(name, ids));
        ProfileStore.Save(profiles);
        SetProfileHint($"已保存「{name}」（{ids.Count} 项）");
    }

    private void ProfileLoad_Click(object sender, RoutedEventArgs e)
    {
        var name = CurrentProfileName;
        var profiles = ProfileStore.Load();
        var hit = profiles.FirstOrDefault(p => p.Name == name);
        if (hit is null)
        {
            var names = profiles.Count == 0 ? "（尚无已保存方案）" : string.Join("、", profiles.Select(p => p.Name));
            DialogService.Warning("配置方案", $"未找到方案「{name}」。已有：{names}");
            return;
        }
        ApplyProfileIds(hit.Ids);
        SetProfileHint($"已载入「{name}」（{hit.Ids.Count} 项）");
    }

    private void ProfileDelete_Click(object sender, RoutedEventArgs e)
    {
        var name = CurrentProfileName;
        var profiles = ProfileStore.Load();
        if (profiles.All(p => p.Name != name))
        {
            DialogService.Warning("配置方案", $"未找到方案「{name}」。");
            return;
        }
        if (!DialogService.Confirm("配置方案", $"删除方案「{name}」？", danger: true))
            return;
        profiles.RemoveAll(p => p.Name == name);
        ProfileStore.Save(profiles);
        SetProfileHint($"已删除「{name}」");
    }

    private void ProfileExport_Click(object sender, RoutedEventArgs e)
    {
        var name = CurrentProfileName;
        var hit = ProfileStore.Load().FirstOrDefault(p => p.Name == name);
        if (hit is null)
        {
            DialogService.Warning("配置方案", $"未找到方案「{name}」，无法导出。");
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出配置方案",
            Filter = "FPS 帧律方案 (*.fpsprofile.json)|*.fpsprofile.json",
            FileName = name + ".fpsprofile.json"
        };
        if (dlg.ShowDialog() != true)
            return;
        System.IO.File.WriteAllText(dlg.FileName,
            System.Text.Json.JsonSerializer.Serialize(hit, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
        SetProfileHint($"已导出到 {dlg.FileName}");
    }

    private void ProfileImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入配置方案",
            Filter = "FPS 帧律方案 (*.fpsprofile.json)|*.fpsprofile.json|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true)
            return;
        try
        {
            var hit = System.Text.Json.JsonSerializer.Deserialize<OptProfile>(
                System.IO.File.ReadAllText(dlg.FileName, Encoding.UTF8));
            if (hit is null || string.IsNullOrWhiteSpace(hit.Name))
                throw new InvalidOperationException("文件内容不是有效的配置方案");
            var profiles = ProfileStore.Load();
            profiles.RemoveAll(p => p.Name == hit.Name);
            profiles.Add(hit);
            ProfileStore.Save(profiles);
            ProfileNameBox.Text = hit.Name;
            ApplyProfileIds(hit.Ids);
            SetProfileHint($"已导入「{hit.Name}」（{hit.Ids.Count} 项）");
        }
        catch (Exception ex)
        {
            DialogService.Warning("配置方案", "导入失败：" + ex.Message);
        }
    }
'''
s = tail[:idx_cls] + '\n' + methods.rstrip('\n') + tail[idx_cls:] + '\n'
save(p, s); print('[CS] ok')

# ============ 3. 测试：分组完备性 ============
p = 'FpsTune.Wpf.Tests/CatalogConsistencyTests.cs'
s = load(p)
if 'Every_item_has_a_known_group' not in s:
    anchor = '    [Fact]\n    public void Reboot_items_match_conservative_snapshot()'
    assert anchor in s
    test = '''    [Fact]
    public void Every_item_has_a_known_group()
    {
        var known = new HashSet<string> { "键鼠", "图形显示", "网络", "电源", "系统与调度" };
        var json = File.ReadAllText(RepoFile("catalog", "catalog.json"));
        using var doc = JsonDocument.Parse(json);
        foreach (var it in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            var g = it.GetProperty("group").GetString() ?? "";
            Assert.Contains(g, known);
        }
    }

'''
    s = s.replace(anchor, test + anchor)
    save(p, s); print('[TEST] 分组守卫 ok')
