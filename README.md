# LKami9幻卡包助手 · 在线模块仓库

LKami9幻卡包助手的**在线模块**仓库（用于添加到 OmniToolbox 的在线模块列表），作者 LKami9。

## 功能

1. **查看卡包内容**：列出每种九宫幻卡包里包含哪些幻卡（含稀有度 ★ 与立绘）。
2. **持有检测**：逐张标出「已持有 / 未持有」，每个卡包给出 `已收集/总数` 进度，并汇总还缺哪些包。
3. **自动化**：
   - 自动使用背包里**尚未持有**的幻卡（已持有的不会浪费）；
   - 自动开背包里的幻卡包，可勾选「只开还有缺卡的卡包」；
   - 两者都有独立开关，间隔可调（默认 100 毫秒）。
4. **游戏内悬停提示**：鼠标放到幻卡包上时，直接在游戏里弹出一个深色小面板，列出这个包里的全部幻卡和持有情况
   （鼠标放到单张幻卡上时，则显示它可能出自哪些卡包）。可以在设置里关掉，也能切换「跟随鼠标 / 贴在物品提示框右侧」
   两种位置，并用左右、上下微调修正偏移。

   检测方式有两套：优先用 Dalamud 的「当前悬停物品」，拿不到时自动退回到读取游戏的 `ItemDetail` 提示框里的物品名，
   设置界面的「提示状态」会显示当前用的是哪一种（`已就绪` / `已就绪（提示框兜底模式）`）。

命令：`/omni LKami9TriadCardPacks scan` 重新检测。

## 在 Omni 中添加

插件设置 → 在线模块 → 填本仓库地址：

```text
https://github.com/LKami9/LKami9-huankabao-zhushou
```

首次安装后模块默认关闭，到「树树妙妙屋 → 在线」里手动启用。

## 目录结构

```text
LKami9-huankabao-zhushou/
  Modules/
    LKami9TriadCardPacks.cs     # 幻卡包内容 / 持有检测 / 自动使用与开包
  TreeHouseModules.json         # 清单（含 SHA256）
  .gitattributes                # 锁定换行符，保证 SHA 稳定
  README.md
```

## 数据来源

- 卡包内容：以灰机 wiki「九宫幻卡获取方式 / 卡包」为准，并与游戏表 `TripleTriadCardResident`
  （获取方式 = 卡包时，`Acquisition` 字段即卡包道具 ID）交叉核对。
- 运行时会把静态表与游戏表**合并取并集**，所以以后版本新增的卡包 / 幻卡会自动补进来。
- 立绘取自 `ui/icon/087000/(87000 + 幻卡编号)_hr1.tex`，持有状态取自 `UnlockState`。

> 如果资料片更新后卡包内容有变动，改 `BuiltinPackContents` 里的数字即可，无需改逻辑。

## 发布 / 更新流程

1. 改完 `Modules/*.cs`
2. 重新计算该文件的 SHA256：

```powershell
(Get-FileHash -LiteralPath '.\Modules\LKami9TriadCardPacks.cs' -Algorithm SHA256).Hash
```

3. 把结果写进 `TreeHouseModules.json` 的 `Sha256`，并**提高 `Version`**
4. 提交推送
5. 在 Omni 里刷新在线模块，确认能下载、启用、保存配置

> 只改 `Sha256` 不提 `Version` 不会触发已安装模块的更新。
> 文件中途被改过（哪怕只是注释或空格）都要重算摘要。

## 本地自检

```powershell
$root = '.'
$manifest = Get-Content -Raw -LiteralPath (Join-Path $root 'TreeHouseModules.json') | ConvertFrom-Json
foreach ($m in $manifest.Modules) {
    $actual = (Get-FileHash -LiteralPath (Join-Path $root $m.File) -Algorithm SHA256).Hash
    '{0}: {1}' -f $m.InternalName, ($actual -eq $m.Sha256)
}
```

输出 `True` 表示本地文件与清单一致。

## 说明

- 在线模块与 Omni 同权限、无独立沙箱，请只从可信来源安装。
- 模块只读写本地游戏状态，不做任何网络请求。
- 「使用幻卡 / 开卡包」是发送正常的使用指令，和手动点背包是同一套行为；不会绕过任何游戏限制。
