using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Lantern.App;

public partial class TerminalWindow
{
    static void Note(Panel panel, string text, bool heading = false) => panel.Children.Add(new TextBlock
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 9),
        FontSize = heading ? 15 : 12, FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = new SolidColorBrush(heading ? Color.FromRgb(255,191,121) : Color.FromRgb(174,191,211))
    });

    void Actions(Panel panel, params (string Label, string Command)[] actions)
    {
        var row = new WrapPanel(); panel.Children.Add(row);
        foreach (var action in actions)
        {
            var button = new Button { Content = action.Label, Tag = action.Command, ToolTip = action.Command,
                Margin = new Thickness(0, 0, 8, 8) };
            button.Click += QuickClick; row.Children.Add(button);
        }
    }

    void BuildManagementPanels()
    {
        Note(WeaponsPanel, dedicated
            ? "独立服务器没有本机玩家，不能用 give 指定给好友发枪。请用经济购买页，让玩家自行购买；也可复制命令到房主的本地练习控制台。"
            : "发放给本机房主，需要已入队且存活。练习发枪请先在「练习辅助」启用 sv_cheats；背包已满时可能掉落在地上。其他玩家不会收到武器。");
        foreach (var group in new[] {
            ("步枪 / 狙击枪", "AK-47:ak47,M4A4:m4a1,M4A1-S:m4a1_silencer,AWP:awp,SSG 08:ssg08,AUG:aug,SG 553:sg556,Galil:galilar,FAMAS:famas,SCAR-20:scar20,G3SG1:g3sg1"),
            ("手枪", "USP-S:usp_silencer,Glock:glock,P2000:hkp2000,P250:p250,沙漠之鹰:deagle,双枪:elite,Tec-9:tec9,Five-SeveN:fiveseven,CZ75:cz75a,左轮:revolver"),
            ("冲锋枪 / 霰弹枪 / 机枪", "MAC-10:mac10,MP9:mp9,MP7:mp7,MP5-SD:mp5sd,UMP-45:ump45,P90:p90,PP-Bizon:bizon,Nova:nova,XM1014:xm1014,MAG-7:mag7,截短霰弹枪:sawedoff,Negev:negev,M249:m249"),
            ("投掷物 / 装备", "烟雾弹:smokegrenade,闪光弹:flashbang,高爆手雷:hegrenade,燃烧瓶:molotov,燃烧弹:incgrenade,诱饵弹:decoy") })
        {
            Note(WeaponsPanel, group.Item1, true);
            var row = new WrapPanel(); WeaponsPanel.Children.Add(row);
            foreach (var item in group.Item2.Split(','))
            {
                var parts = item.Split(':'); var command = "give weapon_" + parts[1];
                var button = new Button { Content = parts[0], ToolTip = command, IsEnabled = !dedicated, Margin = new Thickness(0,0,8,8), Tag = command };
                button.Click += QuickClick; row.Children.Add(button);
            }
        }
        if (!dedicated) Actions(WeaponsPanel, ("防弹衣", "give item_kevlar"), ("防弹衣 + 头盔", "give item_assaultsuit"), ("拆弹器（CT）", "give item_defuser"));

        Note(EconomyPanel, "作用于整个房间。开局金额在重开对局后生效；每回合补助在回合结束后发放，不会立即改动当前余额。");
        Note(EconomyPanel, "金额设置", true);
        var rowMoney = new WrapPanel(); EconomyPanel.Children.Add(rowMoney);
        var start = MoneyInput(rowMoney, "开局", "800");
        var max = MoneyInput(rowMoney, "上限", "16000");
        var award = MoneyInput(rowMoney, "回合补助", "0");
        var apply = new Button { Content = "应用金额", Margin = new Thickness(0,0,8,8) }; rowMoney.Children.Add(apply);
        apply.Click += async (_, _) => {
            if (!int.TryParse(start.Text, out var a) || !int.TryParse(max.Text, out var b) || !int.TryParse(award.Text, out var c)
                || a < 0 || b < 1 || c < 0 || a > b || b > 60000 || c > 60000)
            { ActionState.Text = "金额应为 0–60000 的整数，上限至少为 1，开局金额不能高于上限。"; return; }
            await Run($"mp_maxmoney {b}; mp_startmoney {a}; mp_afterroundmoney {c}");
        };
        Actions(EconomyPanel, ("800 开局 / 16000 上限", "mp_maxmoney 16000; mp_startmoney 800; mp_afterroundmoney 0"),
            ("练习经济 60000", "mp_maxmoney 60000; mp_startmoney 60000; mp_afterroundmoney 60000"), ("重开以应用开局金额", "mp_restartgame 1"));
        Note(EconomyPanel, "购买与出生装备", true);
        Actions(EconomyPanel, ("任意地点购买", "mp_buy_anywhere 1"), ("仅购买区", "mp_buy_anywhere 0"),
            ("购买时间 20 秒", "mp_buytime 20"), ("购买时间 90 秒", "mp_buytime 90"), ("购买时间 60 分钟", "mp_buytime 3600"),
            ("出生不送护甲", "mp_free_armor 0"), ("出生送防弹衣", "mp_free_armor 1"), ("出生送全甲", "mp_free_armor 2"),
            ("读取经济设置", "mp_startmoney; mp_maxmoney; mp_afterroundmoney; mp_buytime; mp_buy_anywhere; mp_free_armor"));

        Note(PracticePanel, "练习权限影响整个房间。下列开关不会自动开启权限或重开对局；关闭权限不会还原经济、回合时间等设置。当前设置以游戏回显为准。");
        Note(PracticePanel, "练习权限 / 弹药", true);
        Actions(PracticePanel, ("启用练习权限", "sv_cheats 1"), ("关闭练习权限", "sv_cheats 0"),
            ("无限弹药 · 无需换弹", "sv_infinite_ammo 1"), ("无限备弹 · 需要换弹", "sv_infinite_ammo 2"), ("正常弹药", "sv_infinite_ammo 0"));
        Note(PracticePanel, "投掷物 / 命中 / 人机（部分需要练习权限）", true);
        Actions(PracticePanel, ("投掷物预览开启", "sv_grenade_trajectory_prac_pipreview 1; sv_grenade_trajectory_prac_trailtime 8"),
            ("投掷物预览关闭", "sv_grenade_trajectory_prac_pipreview 0; sv_grenade_trajectory_prac_trailtime 0"),
            ("重投上一颗手雷", "sv_rethrow_last_grenade"), ("显示弹着点", "sv_showimpacts 1"), ("隐藏弹着点", "sv_showimpacts 0"),
            ("冻结人机", "bot_stop 1"), ("恢复人机移动", "bot_stop 0"));
        Note(PracticePanel, "复活与回合（时间设置从下回合生效）", true);
        Actions(PracticePanel, ("双方死亡后复活", "mp_respawn_on_death_t 1; mp_respawn_on_death_ct 1"),
            ("关闭双方自动复活", "mp_respawn_on_death_t 0; mp_respawn_on_death_ct 0"),
            ("60 分钟回合", "mp_roundtime 60; mp_roundtime_defuse 60; mp_roundtime_hostage 60"),
            ("1 分 55 秒回合", "mp_roundtime 1.916667; mp_roundtime_defuse 1.916667; mp_roundtime_hostage 1.916667"),
            ("取消开局冻结", "mp_freezetime 0"), ("开局冻结 15 秒", "mp_freezetime 15"),
            ("读取练习状态", "sv_cheats; sv_infinite_ammo; sv_showimpacts; bot_stop; mp_respawn_on_death_t; mp_respawn_on_death_ct"));
    }

    static TextBox MoneyInput(Panel row, string title, string value)
    {
        row.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,6,8) });
        var input = new TextBox { Text = value, Width = 85, MaxLength = 5, Margin = new Thickness(0,0,12,8) };
        row.Children.Add(input); return input;
    }

    public async Task CaptureCategories(string directory)
    {
        for (int i = 0; i < CommandTabs.Items.Count; i++)
        {
            CommandTabs.SelectedIndex = i;
            await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle); UpdateLayout();
            var image = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            image.Render(this); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using var output = File.Create(Path.Combine(directory, $"terminal-category-{i}.png")); png.Save(output);
        }
    }
}
