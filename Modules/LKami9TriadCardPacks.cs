using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.IoC;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

using Lumina.Excel.Sheets;

using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;

namespace LKami9.TriadCardPacks;

/// <summary>
/// LKami9幻卡包助手（在线模块版）
/// 显示每个九宫幻卡包里包含哪些幻卡、自己已持有 / 未持有，并可自动使用背包中未持有的幻卡、自动开幻卡包。
/// </summary>
public sealed class LKami9TriadCardPacks : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "LKami9幻卡包助手",
        Description = "列出各幻卡包内含的幻卡与持有情况，可自动使用背包中未持有的幻卡、自动开幻卡包。",
        Category    = ModuleCategory.Item,
        Author      = "LKami9",
        Commands    = [new("重新检测幻卡包与幻卡", "/omni LKami9TriadCardPacks scan")],
    };

    public override bool HasSettings => true;

    // 宿主按“以 Config 结尾的成员”绑定已保存的配置；不能是 readonly，宿主需要给它赋值
    private LKami9TriadCardPacksConfig config = new();

    /// <summary>TripleTriadCardObtain 里“以下卡包中随机获得”那一行的行号（国服 7.x 数据）。</summary>
    private const uint CardPackObtainRowId = 8;

    /// <summary>幻卡立绘图标：ui/icon/087000/(87000 + 幻卡编号)_hr1.tex</summary>
    private const uint CardArtIconBase = 87000;

    /// <summary>
    /// 各幻卡包的内容（数据来自灰机 wiki「九宫幻卡获取方式 / 卡包」，已与游戏内 TripleTriadCardResident 交叉核对，
    /// 静态表是游戏数据的超集）。运行时还会与游戏数据合并，新版本新增的卡包会自动补进来。
    /// </summary>
    private static readonly (uint PackItemId, uint[] CardIds)[] BuiltinPackContents =
    [
        (10077u, [51, 55, 57, 59, 60, 63, 69, 71, 77, 80, 138, 153]),                          // 白金包
        (10128u, [4, 5, 8, 11, 12, 24, 25, 26, 27, 38, 147, 156, 157, 159, 162]),              // 铜包
        (10129u, [14, 15, 16, 17, 19, 21, 40, 41, 42, 43, 50, 56, 57, 129]),                   // 银包
        (10130u, [28, 29, 30, 34, 51, 53, 54, 55, 58, 65, 66, 67, 68, 76, 122]),               // 金包
        (13380u, [39, 52, 70, 72, 73, 108, 109, 113, 123]),                                     // 灵银包
        (17690u, [51, 53, 57, 63, 80]),                                                         // 曼德维尔铜包
        (17691u, [51, 53, 57, 63, 80]),                                                         // 曼德维尔银包
        (17692u, [51, 53, 57, 63, 80]),                                                         // 曼德维尔金包
        (17693u, [51, 55, 57, 63, 71]),                                                         // 命运神铜包
        (17694u, [51, 55, 57, 63, 71]),                                                         // 命运神银包
        (17695u, [51, 55, 57, 63, 71]),                                                         // 命运神金包
        (17696u, [51, 52, 57, 63, 69]),                                                         // 奥兰铜包
        (17697u, [51, 52, 57, 63, 69]),                                                         // 奥兰银包
        (17698u, [51, 52, 57, 63, 69]),                                                         // 奥兰金包
        (17699u, [51, 54, 57, 63, 77]),                                                         // 罗薇娜铜包
        (17700u, [51, 54, 57, 63, 77]),                                                         // 罗薇娜银包
        (17701u, [51, 54, 57, 63, 77]),                                                         // 罗薇娜金包
        (17702u, [119, 171, 172, 175, 176, 180]),                                               // 帝国包
        (28652u, [260, 261, 262, 263, 267, 273, 275, 277, 278, 280, 281]),                     // 梦想包
    ];

    private readonly Dictionary<uint, List<uint>> packContents = [];
    private readonly Dictionary<string, uint> packItemByName = [];
    private readonly Dictionary<string, uint> cardItemByName = [];
    private readonly Dictionary<uint, uint> cardItemIds = [];
    private readonly Dictionary<uint, uint> cardByItemId = [];
    private readonly Dictionary<uint, string> cardNames = [];
    private readonly Dictionary<uint, byte> cardStars = [];

    private readonly List<PackRow> rows = [];
    private readonly Dictionary<uint, int> bagCounts = [];
    private bool catalogBuilt;

    private int bagCardCount;
    private int bagPackCount;
    private string status = "尚未检测。";

    private string? copiedText;
    private double copiedAt;

    private bool? forcePackOpen;

    // 悬停提示（游戏内物品提示框旁边）
    private IGameGui? gameGui;
    private bool hoverHooked;
    private string hoverStatus = "未初始化";
    private IntPtr itemDetailAddon;
    private string? itemDetailName;

    // 自动使用 / 自动开包（待确认机制）
    private uint pendingItemId;
    private int pendingInitialCount;
    private uint lastAttemptItemId;
    private int pendingAttempts;
    private DateTime pendingSinceUtc;
    private DateTime nextActionUtc = DateTime.MinValue;
    private DateTime nextScanUtc = DateTime.MinValue;

    #region 生命周期

    protected override void OnEnable()
    {
        DalamudServices.Framework.Update += this.OnFrameworkUpdate;
        this.EnsureHoverSupport();
        this.status = "已启用，正在检测…";
        this.Scan();
    }

    protected override void OnDisable()
    {
        DalamudServices.Framework.Update -= this.OnFrameworkUpdate;
        this.ReleaseHoverSupport();
        this.pendingItemId = 0;
    }

    protected override void OnDispose() => this.ReleaseHoverSupport();

    public override bool ResetSettings()
    {
        this.config.ShowOwned          = true;
        this.config.AutoUseCards       = false;
        this.config.AutoOpenPacks      = false;
        this.config.OnlyIncomplete     = true;
        this.config.IntervalMs         = 100;
        this.config.HoverInfo          = true;
        this.config.HoverAnchor        = 0;
        this.config.HoverOffsetX       = 0;
        this.config.HoverOffsetY       = 0;
        this.config.HoverShowOwned     = true;
        return true;
    }

    public override bool TryHandleCommand(string arguments)
    {
        if (!string.Equals(arguments.Trim(), "scan", StringComparison.OrdinalIgnoreCase))
            return false;

        this.Scan();
        return true;
    }

    #endregion

    #region 数据

    private void EnsureCatalog()
    {
        if (this.catalogBuilt)
            return;

        this.catalogBuilt = true;

        try
        {
            var cards = DalamudServices.DataManager.GetExcelSheet<TripleTriadCard>();
            var items = DalamudServices.DataManager.GetExcelSheet<Item>();

            // 幻卡编号 -> 名字 / 稀有度
            foreach (var card in cards)
            {
                if (card.RowId == 0)
                    continue;

                this.cardNames[card.RowId] = card.Name.ToString();
            }

            var residents = DalamudServices.DataManager.GetExcelSheet<TripleTriadCardResident>();
            var rarities = DalamudServices.DataManager.GetExcelSheet<TripleTriadCardRarity>();

            foreach (var resident in residents)
            {
                if (!resident.TripleTriadCardRarity.IsValid)
                    continue;

                if (rarities.TryGetRow(resident.TripleTriadCardRarity.RowId, out var rarity))
                    this.cardStars[resident.RowId] = rarity.Stars;
            }

            // 道具 -> 幻卡编号（“九宫幻卡：XXX”与幻卡同名）
            var cardIdsByName = new Dictionary<string, uint>();
            foreach (var kv in this.cardNames)
                cardIdsByName[kv.Value] = kv.Key;

            const string prefix = "九宫幻卡：";
            foreach (var item in items)
            {
                if (item.RowId == 0)
                    continue;

                var name = item.Name.ToString();
                if (!name.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                if (cardIdsByName.TryGetValue(name[prefix.Length..], out var cardId))
                {
                    this.cardItemIds[cardId] = item.RowId;
                    this.cardByItemId[item.RowId] = cardId;
                    this.cardItemByName[name] = item.RowId;
                }
            }

            // 静态表
            foreach (var (packItemId, cardIds) in BuiltinPackContents)
            {
                if (!this.packContents.TryGetValue(packItemId, out var list))
                    this.packContents[packItemId] = list = [];

                foreach (var cardId in cardIds)
                {
                    if (!list.Contains(cardId))
                        list.Add(cardId);
                }
            }

            // 游戏数据（获取方式 = 卡包时，Acquisition 字段存的就是卡包道具 ID）
            foreach (var resident in residents)
            {
                if (!resident.AcquisitionType.IsValid || resident.AcquisitionType.RowId != CardPackObtainRowId)
                    continue;

                var packItemId = resident.Acquisition.RowId;
                if (packItemId == 0)
                    continue;

                if (!this.packContents.TryGetValue(packItemId, out var list))
                    this.packContents[packItemId] = list = [];

                if (!list.Contains(resident.RowId))
                    list.Add(resident.RowId);
            }

            // 卡包道具名 -> 道具 ID（供提示框兜底匹配）
            foreach (var packItemId in this.packContents.Keys)
            {
                if (items.TryGetRow(packItemId, out var packItem))
                    this.packItemByName[packItem.Name.ToString()] = packItemId;
            }
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Error(ex, "[LKami9幻卡包助手] 建立幻卡数据失败");
        }
    }

    private void Scan()
    {
        this.EnsureCatalog();
        this.RefreshBagCounts();

        var items = DalamudServices.DataManager.GetExcelSheet<Item>();
        this.rows.Clear();

        foreach (var kv in this.packContents)
        {
            if (!items.TryGetRow(kv.Key, out var packItem))
                continue;

            var pack = new PackInfo(kv.Key, packItem.Name.ToString(), packItem.Icon);
            var cardList = new List<CardInfo>(kv.Value.Count);

            foreach (var cardId in kv.Value)
            {
                var name  = this.cardNames.GetValueOrDefault(cardId, $"#{cardId}");
                var stars = this.cardStars.GetValueOrDefault(cardId, (byte)0);
                var itemId = this.cardItemIds.GetValueOrDefault(cardId, 0u);

                cardList.Add(new CardInfo(cardId, name, stars, itemId, this.IsCardOwned(cardId)));
            }

            cardList.Sort((a, b) =>
            {
                var byStar = b.Stars.CompareTo(a.Stars);
                return byStar != 0 ? byStar : a.CardId.CompareTo(b.CardId);
            });

            this.rows.Add(new PackRow(pack, this.bagCounts.GetValueOrDefault(pack.ItemId), cardList));
        }

        this.rows.Sort((a, b) => a.Pack.ItemId.CompareTo(b.Pack.ItemId));

        this.bagPackCount = this.rows.Sum(r => r.BagCount);
        this.bagCardCount = 0;

        foreach (var kv in this.cardItemIds)
        {
            if (!this.IsCardOwned(kv.Key))
                this.bagCardCount += this.bagCounts.GetValueOrDefault(kv.Value);
        }

        var total  = this.rows.Sum(r => r.Cards.Count);
        var owned  = this.rows.Sum(r => r.Owned);
        var packs  = this.rows.Count;
        var canOpen = this.rows.Count(r => r.BagCount > 0 && r.Missing > 0);

        if (this.pendingItemId == 0)
        {
            this.status =
                $"卡包 {packs} 种　·　内含幻卡已持有 {owned}/{total}　·　背包中卡包 {this.bagPackCount} 个（其中还有缺卡的可开 {canOpen} 种）";
        }
    }

    private bool IsCardOwned(uint cardId)
    {
        try
        {
            var cards = DalamudServices.DataManager.GetExcelSheet<TripleTriadCard>();
            return cards.TryGetRow(cardId, out var card) && DalamudServices.UnlockState.IsTripleTriadCardUnlocked(card);
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Debug(ex, $"[LKami9幻卡包助手] 读取幻卡 {cardId} 状态失败");
            return false;
        }
    }

    #endregion

    #region 背包

    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private static unsafe InventoryContainer* GetContainer(InventoryType type)
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
            return null;

        var container = manager->GetInventoryContainer(type);
        return container != null && container->IsLoaded ? container : null;
    }

    private unsafe int CountInBags(uint itemId)
    {
        if (itemId == 0)
            return 0;

        var count = 0;

        foreach (var bag in PlayerBags)
        {
            var container = GetContainer(bag);
            if (container == null)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->ItemId == itemId)
                    count += (int)slot->GetQuantity();
            }
        }

        return count;
    }

    /// <summary>一次遍历背包，缓存当前所有幻卡 / 卡包的数量，供界面与检测复用。</summary>
    private unsafe void RefreshBagCounts()
    {
        this.bagCounts.Clear();

        foreach (var bag in PlayerBags)
        {
            var container = GetContainer(bag);
            if (container == null)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0)
                    continue;

                var itemId = slot->ItemId;
                if (!this.cardByItemId.ContainsKey(itemId) && !this.packContents.ContainsKey(itemId))
                    continue;

                this.bagCounts[itemId] = this.bagCounts.GetValueOrDefault(itemId) + (int)slot->GetQuantity();
            }
        }
    }

    #endregion

    #region 自动使用 / 自动开包

    private static readonly TimeSpan NoEffectWindow = TimeSpan.FromSeconds(0.8);
    private static readonly TimeSpan CastTimeout    = TimeSpan.FromSeconds(6);

    private int IntervalMs => Math.Clamp(this.config.IntervalMs, 50, 1_500);

    private void OnFrameworkUpdate(IFramework framework)
    {
        // 定期刷新持有状态，保证界面与自动逻辑用的是最新数据
        if (DateTime.UtcNow >= this.nextScanUtc)
        {
            this.nextScanUtc = DateTime.UtcNow.AddSeconds(2);
            this.Scan();
        }

        if (!this.config.AutoUseCards && !this.config.AutoOpenPacks)
            return;

        if (!this.CheckPending())
            return;

        if (DateTime.UtcNow < this.nextActionUtc)
            return;

        if (this.config.AutoUseCards && this.TryUseNext(isPack: false))
            return;

        if (this.config.AutoOpenPacks)
            this.TryUseNext(isPack: true);
    }

    private bool CheckPending()
    {
        if (this.pendingItemId == 0)
            return true;

        var elapsed = DateTime.UtcNow - this.pendingSinceUtc;
        var current = this.CountInBags(this.pendingItemId);

        // 数量变少（或直接消失）就说明这一张已经被吃掉了
        if (current < this.pendingInitialCount)
        {
            this.pendingItemId  = 0;
            this.pendingAttempts = 0;
            this.nextActionUtc  = DateTime.UtcNow.AddMilliseconds(this.IntervalMs);
            return true;
        }

        if (elapsed > NoEffectWindow)
        {
            // 完全没反应 → 请求被游戏丢掉，重发；连续多次没反应就放慢，避免空转刷屏
            this.pendingItemId = 0;
            var delay = elapsed > CastTimeout ? 1_000 : this.pendingAttempts >= 6 ? 2_000 : 150;
            this.nextActionUtc = DateTime.UtcNow.AddMilliseconds(delay);
        }

        return false;
    }

    private unsafe bool TryUseNext(bool isPack)
    {
        if (this.pendingItemId != 0)
            return false;

        var agent = AgentInventoryContext.Instance();
        if (agent == null)
            return false;

        foreach (var bag in PlayerBags)
        {
            var container = GetContainer(bag);
            if (container == null)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0)
                    continue;

                var itemId = slot->ItemId;

                if (!this.IsCandidate(itemId, isPack))
                    continue;

                try
                {
                    agent->UseItem(itemId, bag, (uint)i);
                }
                catch (Exception ex)
                {
                    DalamudServices.PluginLog.Error(ex, "[LKami9幻卡包助手] 使用道具失败");
                    return false;
                }

                this.pendingAttempts     = itemId == this.lastAttemptItemId ? this.pendingAttempts + 1 : 1;
                this.lastAttemptItemId   = itemId;
                this.pendingItemId       = itemId;
                this.pendingInitialCount = this.CountInBags(itemId);
                this.pendingSinceUtc     = DateTime.UtcNow;
                this.nextActionUtc       = DateTime.UtcNow.AddMilliseconds(this.IntervalMs);
                this.status              = isPack ? $"正在打开卡包（背包剩余 {this.pendingInitialCount}）…" : "正在使用幻卡…";
                return true;
            }
        }

        return false;
    }

    private bool IsCandidate(uint itemId, bool isPack)
    {
        if (isPack)
        {
            var row = this.rows.FirstOrDefault(r => r.Pack.ItemId == itemId);
            if (row is null)
                return false;

            // 只开还有缺卡的包
            return !this.config.OnlyIncomplete || row.Missing > 0;
        }

        return this.cardByItemId.TryGetValue(itemId, out var cardId) && !this.IsCardOwned(cardId);
    }

    #endregion

    #region 界面

    public override bool DrawSettings()
    {
        var changed = false;

        this.RefreshBagCounts();

        ImGui.TextWrapped(this.status);

        if (this.copiedText is { } copied && (ImGui.GetTime() - this.copiedAt) < 2.5)
            ImGui.TextColored(new System.Numerics.Vector4(0.5f, 1f, 0.6f, 1f), $"已复制：{copied}");

        ImGui.Spacing();

        if (ImGui.Button("立即检测"))
        {
            this.Scan();
            this.copiedText = null;
        }

        ImGui.SameLine();
        var autoCards = this.config.AutoUseCards;
        if (ImGui.Checkbox("自动使用未持有的幻卡", ref autoCards))
        {
            this.config.AutoUseCards = autoCards;
            changed = true;
        }

        ImGui.SameLine();
        var autoPacks = this.config.AutoOpenPacks;
        if (ImGui.Checkbox("自动开卡包", ref autoPacks))
        {
            this.config.AutoOpenPacks = autoPacks;
            changed = true;
        }

        var showOwned = this.config.ShowOwned;
        if (ImGui.Checkbox("显示已持有", ref showOwned))
        {
            this.config.ShowOwned = showOwned;
            changed = true;
        }

        ImGui.SameLine();
        var onlyIncomplete = this.config.OnlyIncomplete;
        if (ImGui.Checkbox("只开还有缺卡的卡包", ref onlyIncomplete))
        {
            this.config.OnlyIncomplete = onlyIncomplete;
            changed = true;
        }

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        var interval = this.config.IntervalMs;
        if (ImGui.SliderInt("每个之间的间隔 (毫秒)", ref interval, 50, 1_500))
        {
            this.config.IntervalMs = interval;
            changed = true;
        }

        var hoverInfo = this.config.HoverInfo;
        if (ImGui.Checkbox("悬停幻卡包 / 幻卡时在游戏里弹出内容提示", ref hoverInfo))
        {
            this.config.HoverInfo = hoverInfo;
            changed = true;
        }

        if (this.config.HoverInfo)
        {
            ImGui.SameLine();
            var hoverShowOwned = this.config.HoverShowOwned;
            if (ImGui.Checkbox("提示里显示已持有", ref hoverShowOwned))
            {
                this.config.HoverShowOwned = hoverShowOwned;
                changed = true;
            }

            var anchor = this.config.HoverAnchor;
            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            if (ImGui.Combo("提示位置", ref anchor, "跟随鼠标\0贴在物品提示框右侧\0", 2))
            {
                this.config.HoverAnchor = anchor;
                changed = true;
            }

            ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
            var offsetX = this.config.HoverOffsetX;
            if (ImGui.DragInt("左右微调", ref offsetX, 1f, -600, 600))
            {
                this.config.HoverOffsetX = offsetX;
                changed = true;
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
            var offsetY = this.config.HoverOffsetY;
            if (ImGui.DragInt("上下微调", ref offsetY, 1f, -600, 600))
            {
                this.config.HoverOffsetY = offsetY;
                changed = true;
            }

            ImGui.SameLine();
            ImGui.TextDisabled($"提示状态：{this.hoverStatus}");
        }

        if (ImGui.Button("全部展开"))
            this.forcePackOpen = true;

        ImGui.SameLine();
        if (ImGui.Button("全部收起"))
            this.forcePackOpen = false;

        ImGui.SameLine();
        ImGui.TextDisabled($"背包中未持有的幻卡 {this.bagCardCount} 张　·　卡包 {this.bagPackCount} 个");

        ImGui.Separator();

        using (var child = ImRaii.Child("##lkTriadPacks", new System.Numerics.Vector2(0, -150 * ImGuiHelpers.GlobalScale), true))
        {
            if (child.Success)
            {
                changed |= this.DrawSummary();
                ImGui.Spacing();

                foreach (var row in this.rows)
                    changed |= this.DrawPack(row);
            }
        }

        this.forcePackOpen = null;
        return changed;
    }

    private bool DrawSummary()
    {
        var total = this.rows.Sum(r => r.Cards.Count);
        var owned = this.rows.Sum(r => r.Owned);

        if (!ImGui.CollapsingHeader($"汇总：幻卡包收集进度 {owned}/{total}###lkTriadSummary", ImGuiTreeNodeFlags.DefaultOpen))
            return false;

        ImGui.Indent();

        foreach (var row in this.rows.Where(r => r.Missing > 0).OrderByDescending(r => r.Missing))
        {
            ImGui.TextUnformatted($"{row.Pack.Name}：还缺 {row.Missing} 张");
            ImGui.SameLine();
            ImGui.TextDisabled(row.BagCount > 0 ? $"（背包里还有 {row.BagCount} 个）" : "（背包里没有）");
        }

        if (this.rows.All(r => r.Missing == 0))
            ImGui.TextDisabled("所有卡包内的幻卡都已收集齐全。");

        ImGui.Unindent();
        return false;
    }

    private bool DrawPack(PackRow row)
    {
        if (this.forcePackOpen is { } force)
            ImGui.SetNextItemOpen(force, ImGuiCond.Always);

        var bagText = row.BagCount > 0 ? $"　背包 ×{row.BagCount}" : string.Empty;
        var flag = row.BagCount > 0 && row.Missing > 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;

        if (!ImGui.CollapsingHeader($"{row.Pack.Name}　{row.Owned}/{row.Cards.Count}{bagText}###lkTriadPack{row.Pack.ItemId}", flag))
            return false;

        var changed = false;
        ImGui.Indent();

        this.DrawItemIcon(row.Pack.Icon, 22);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled($"卡包道具 ID：{row.Pack.ItemId}");

        ImGui.Spacing();

        foreach (var card in row.Cards)
        {
            if (!this.config.ShowOwned && card.Owned)
                continue;

            this.DrawCard(card);
        }

        ImGui.Unindent();
        return changed;
    }

    private void DrawCard(CardInfo card)
    {
        var artId = CardArtIconBase + card.CardId;
        this.DrawItemIcon(artId, 0, 26, 32);
        ImGui.SameLine();

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(card.Name);
        this.ClickToCopy(card.Name);

        if (card.Stars > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0.4f, 1f), new string('★', card.Stars));
        }

        ImGui.SameLine();
        if (card.Owned)
        {
            ImGui.TextColored(new System.Numerics.Vector4(0.5f, 1f, 0.5f, 1f), "已持有");
        }
        else
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.55f, 0.55f, 1f), "未持有");
        }

        var bag = card.ItemId == 0 ? 0 : this.bagCounts.GetValueOrDefault(card.ItemId);
        if (bag > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"背包 ×{bag}");
        }
    }

    private void DrawItemIcon(uint iconId, float size, float width = 0, float height = 0)
    {
        if (iconId == 0)
            return;

        try
        {
            var folder = (iconId / 1000) * 1000;
            var texture = DalamudServices.TextureProvider.GetFromGame($"ui/icon/{folder:D6}/{iconId:D6}_hr1.tex").GetWrapOrEmpty();

            var scale = ImGuiHelpers.GlobalScale;
            var drawSize = width > 0 && height > 0
                               ? new System.Numerics.Vector2(width * scale, height * scale)
                               : new System.Numerics.Vector2(size * scale, size * scale);

            ImGui.Image(texture.Handle, drawSize);
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Debug(ex, $"[LKami9幻卡包助手] 读取图标 {iconId} 失败");
        }
    }

    private void ClickToCopy(string? text)
    {
        if (text is null)
            return;

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("点击复制：" + text);

        if (!ImGui.IsItemClicked())
            return;

        ImGui.SetClipboardText(text);
        this.copiedText = text;
        this.copiedAt   = ImGui.GetTime();
    }

    #endregion

    #region 悬停提示

    private void EnsureHoverSupport()
    {
        if (this.hoverHooked)
            return;

        // Omni 的 DalamudServices 没有暴露 IGameGui，这里用标准的 [PluginService] 注入补上。
        // 万一注入失败也不影响使用：下面还有读取 ItemDetail 提示框物品名的兜底方案。
        try
        {
            DalamudServices.PluginInterface.Create<LKami9TriadCardPacksHostServices>();
            this.gameGui = LKami9TriadCardPacksHostServices.GameGui;
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Debug(ex, "[LKami9幻卡包助手] PluginService 注入 IGameGui 失败，改用提示框兜底");
        }

        try
        {
            DalamudServices.PluginInterface.UiBuilder.Draw += this.OnDrawUi;
        }
        catch (Exception ex)
        {
            this.hoverStatus = "初始化失败：" + ex.Message;
            DalamudServices.PluginLog.Error(ex, "[LKami9幻卡包助手] 挂载悬停提示失败");
            return;
        }

        try
        {
            DalamudServices.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "ItemDetail", this.OnItemDetailEvent);
            DalamudServices.AddonLifecycle.RegisterListener(AddonEvent.PreHide, "ItemDetail", this.OnItemDetailHidden);
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Debug(ex, "[LKami9幻卡包助手] 挂 AddonLifecycle 失败");
        }

        this.hoverHooked = true;
        this.hoverStatus = this.gameGui is null ? "已就绪（提示框兜底模式）" : "已就绪";
    }

    private void ReleaseHoverSupport()
    {
        if (!this.hoverHooked)
            return;

        try
        {
            DalamudServices.PluginInterface.UiBuilder.Draw -= this.OnDrawUi;
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Debug(ex, "[LKami9幻卡包助手] 卸载悬停提示失败");
        }

        try
        {
            DalamudServices.AddonLifecycle.UnregisterListener(AddonEvent.PostDraw, "ItemDetail", this.OnItemDetailEvent);
            DalamudServices.AddonLifecycle.UnregisterListener(AddonEvent.PreHide, "ItemDetail", this.OnItemDetailHidden);
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Debug(ex, "[LKami9幻卡包助手] 卸载 AddonLifecycle 失败");
        }

        this.itemDetailAddon = IntPtr.Zero;
        this.itemDetailName  = null;
        this.hoverHooked = false;
    }

    private unsafe void OnItemDetailEvent(AddonEvent type, AddonArgs args)
    {
        try
        {
            this.itemDetailAddon = args.Addon.Address;

            if (this.gameGui is not null)
                return;

            var addon = (AddonItemDetail*)args.Addon.Address;
            this.itemDetailName = addon != null ? addon->ItemName.ToString() : null;
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Debug(ex, "[LKami9幻卡包助手] 读取 ItemDetail 失败");
        }
    }

    private void OnItemDetailHidden(AddonEvent type, AddonArgs args)
    {
        this.itemDetailName = null;
    }

    private void OnDrawUi()
    {
        if (!this.config.HoverInfo || !this.hoverHooked)
            return;

        try
        {
            this.DrawHoverPanel();
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Debug(ex, "[LKami9幻卡包助手] 绘制悬停提示失败");
        }
    }

    /// <summary>取当前鼠标悬停的道具 ID（0 表示没有）。</summary>
    private uint GetHoveredItemId()
    {
        var raw = this.gameGui?.HoveredItem ?? 0;
        if (raw != 0)
        {
            var low = (uint)(raw & 0xFFFF_FFFF);
            if (low != 0 && (this.packContents.ContainsKey(low) || this.cardByItemId.ContainsKey(low)))
                return low;

            var high = (uint)(raw >> 32);
            if (high != 0 && (this.packContents.ContainsKey(high) || this.cardByItemId.ContainsKey(high)))
                return high;
        }

        // 兜底：直接拿 ItemDetail 提示框上显示的物品名去匹配
        if (this.itemDetailName is { Length: > 0 } name && this.IsItemDetailVisible())
        {
            if (this.packItemByName.TryGetValue(name, out var packId))
                return packId;

            if (this.cardItemByName.TryGetValue(name, out var cardItemId))
                return cardItemId;
        }

        return 0;
    }

    private unsafe bool IsItemDetailVisible()
    {
        if (this.itemDetailAddon == IntPtr.Zero)
            return false;

        var addon = (AtkUnitBase*)this.itemDetailAddon;
        return addon != null && addon->IsVisible;
    }

    private unsafe bool TryGetItemDetailAnchor(out System.Numerics.Vector2 anchor)
    {
        anchor = default;

        var address = this.itemDetailAddon;
        if (address == IntPtr.Zero)
            address = this.gameGui?.GetAddonByName("ItemDetail", 1).Address ?? IntPtr.Zero;

        if (address == IntPtr.Zero)
            return false;

        var addon = (AtkUnitBase*)address;
        if (addon == null || !addon->IsVisible || addon->RootNode == null)
            return false;

        anchor = new System.Numerics.Vector2
        (
            addon->RootNode->GetXFloat() + addon->RootNode->GetWidth(),
            addon->RootNode->GetYFloat()
        );

        return true;
    }

    private void DrawHoverPanel()
    {
        var itemId = this.GetHoveredItemId();
        if (itemId == 0)
            return;

        var scale   = ImGuiHelpers.GlobalScale;
        var offset  = new System.Numerics.Vector2(this.config.HoverOffsetX, this.config.HoverOffsetY) * scale;
        var pivot   = new System.Numerics.Vector2(1f, 1f);
        var pos     = ImGui.GetMousePos() - (new System.Numerics.Vector2(16, 16) * scale) + offset;

        if (this.config.HoverAnchor == 1 && this.TryGetItemDetailAnchor(out var tip))
        {
            pivot = new System.Numerics.Vector2(0f, 0f);
            pos   = tip + (new System.Numerics.Vector2(6, 0) * scale) + offset;
        }

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoInputs
            | ImGuiWindowFlags.NoSavedSettings;

        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always, pivot);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new System.Numerics.Vector4(0.05f, 0.05f, 0.07f, 0.95f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new System.Numerics.Vector2(9, 7) * scale);

        if (ImGui.Begin("###lkTriadHover", flags))
        {
            if (this.packContents.ContainsKey(itemId))
                this.DrawHoverPack(itemId);
            else
                this.DrawHoverCard(itemId);
        }

        ImGui.End();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    private void DrawHoverPack(uint packItemId)
    {
        var row = this.rows.FirstOrDefault(r => r.Pack.ItemId == packItemId);
        if (row is null)
            return;

        ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0.4f, 1f), row.Pack.Name);
        ImGui.SameLine();
        ImGui.TextDisabled($"已收集 {row.Owned}/{row.Cards.Count}");

        if (row.BagCount > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"背包 ×{row.BagCount}");
        }

        ImGui.Separator();

        foreach (var card in row.Cards)
        {
            if (!this.config.HoverShowOwned && card.Owned)
                continue;

            this.DrawHoverCardRow(card, showBag: true);
        }
    }

    private void DrawHoverCard(uint cardItemId)
    {
        if (!this.cardByItemId.TryGetValue(cardItemId, out var cardId))
            return;

        var name  = this.cardNames.GetValueOrDefault(cardId, $"#{cardId}");
        var stars = this.cardStars.GetValueOrDefault(cardId, (byte)0);
        var owned = this.IsCardOwned(cardId);

        var packs = this.rows.Where(r => r.Cards.Any(c => c.CardId == cardId)).ToList();

        ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0.4f, 1f), name);
        if (stars > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0.4f, 1f), new string('★', stars));
        }

        ImGui.SameLine();
        ImGui.TextColored
        (
            owned
                ? new System.Numerics.Vector4(0.5f, 1f, 0.5f, 1f)
                : new System.Numerics.Vector4(1f, 0.55f, 0.55f, 1f),
            owned ? "已持有" : "未持有"
        );

        ImGui.Separator();

        if (packs.Count == 0)
        {
            ImGui.TextDisabled("这张幻卡不在已知的卡包内。");
            return;
        }

        ImGui.TextDisabled("可能出自这些卡包：");

        foreach (var pack in packs)
        {
            this.DrawItemIcon(pack.Pack.Icon, 0, 20, 20);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(pack.Pack.Name);
        }
    }

    private void DrawHoverCardRow(CardInfo card, bool showBag)
    {
        this.DrawItemIcon(CardArtIconBase + card.CardId, 0, 20, 25);
        ImGui.SameLine();

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(card.Name);

        if (card.Stars > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0.4f, 1f), new string('★', card.Stars));
        }

        ImGui.SameLine();
        ImGui.TextColored
        (
            card.Owned
                ? new System.Numerics.Vector4(0.5f, 1f, 0.5f, 1f)
                : new System.Numerics.Vector4(1f, 0.55f, 0.55f, 1f),
            card.Owned ? "已持有" : "未持有"
        );

        if (!showBag || card.ItemId == 0)
            return;

        var bag = this.bagCounts.GetValueOrDefault(card.ItemId);
        if (bag > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"背包 ×{bag}");
        }
    }

    #endregion

    #region 数据模型

    private sealed record PackInfo(uint ItemId, string Name, uint Icon);

    private sealed record CardInfo(uint CardId, string Name, byte Stars, uint ItemId, bool Owned);

    private sealed record PackRow(PackInfo Pack, int BagCount, List<CardInfo> Cards)
    {
        public int Owned => this.Cards.Count(c => c.Owned);

        public int Missing => this.Cards.Count - this.Owned;
    }

    #endregion
}

/// <summary>Omni 的 DalamudServices 没有暴露 IGameGui，这里用标准的 [PluginService] 注入补上。</summary>
public sealed class LKami9TriadCardPacksHostServices
{
    [PluginService]
    public static IGameGui GameGui { get; private set; } = null!;
}

[Serializable]
public sealed class LKami9TriadCardPacksConfig
{
    public bool ShowOwned { get; set; } = true;

    public bool AutoUseCards { get; set; }

    public bool AutoOpenPacks { get; set; }

    public bool OnlyIncomplete { get; set; } = true;

    public int IntervalMs { get; set; } = 100;

    public bool HoverInfo { get; set; } = true;

    /// <summary>0 = 跟随鼠标，1 = 贴在物品提示框右侧。</summary>
    public int HoverAnchor { get; set; }

    public int HoverOffsetX { get; set; }

    public int HoverOffsetY { get; set; }

    public bool HoverShowOwned { get; set; } = true;
}
