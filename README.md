# 动态出率 / DynamicCardOdds

《杀戮尖塔 2》故障机器人卡牌奖励学习模组。它从 BetterDefect v0.11.35 起独立发布，不依赖 BetterDefect 或 BaseLib。

## 最新版本

- [v1.0.6](https://github.com/Yummn/sts2-dynamic-card-odds/releases/tag/v1.0.6)：经 Android v110.1 实机验证，禁用遮罩改为不参与布局计算的 Polygon2D。修复旧 ColorRect 遮罩导致卡牌网格压缩成细条及遮罩向右错位的问题；禁用、恢复、进入详情页均已实机验证，原禁用记录保持不变。

- [v1.0.3](https://github.com/Yummn/sts2-dynamic-card-odds/releases/tag/v1.0.3)：修复禁用卡牌的黑色遮罩在卡牌池复用与重新布局后尺寸变为零、无法显示的问题。遮罩改用固定卡牌矩形并置于卡牌顶层。Android v103、Android v110.1 与 PC v107.1 均已编译，PC 启动加载验证通过。

- [v1.0.2](https://github.com/Yummn/sts2-dynamic-card-odds/releases/tag/v1.0.2)：修复从百科大全打开卡牌详情后，列表卡牌仍显示禁用出率按钮的问题。详情页打开时会立即清理按钮和遮罩，并阻止转场期间的延迟刷新再次注入控件。Android v103、Android v110.1 与 PC v107.1 分别编译。

## 功能

- 只学习“正好三张、且全部为机器人卡牌”的普通卡牌奖励；无色、混色和四张奖励不会污染统计。
- 以卡牌被展示的次数为曝光量，以实际选择为正样本；跳过一次按每张牌 `0.5` 次曝光处理。
- 每种稀有度独立统计并在同稀有度候选内归一，不改变白、蓝、金卡奖励本身的稀有度抽取概率。
- 区分当前牌组已有 `0 / 1 / 2+` 张时的需求，避免“已经拿够了仍越拿越容易出”。
- 自动从当前牌组和历史选择中学习卡牌共现关系，一张牌可以同时服务多个流派，不需要手工分类。
- 使用贝叶斯平滑、低曝光探索补偿和每局 `10%` 的历史衰减，让冷门牌仍有重新被发现的机会。
- 常态卡面不再显示概率；只有从百科大全打开卡牌详情时，才在卡牌旁边的独立信息窗显示“首张 / 第2张 / 3张+”三个动态倍率。
- 百科大全内可无限量禁用机器人卡牌；禁用后奖励权重为 `0`，不消耗 BetterDefect 改造点数。
- 禁用黑色遮罩改为持久节点，不再和游戏卡牌动画反复重建，修复闪烁。
- 首次启动自动迁移旧 BetterDefect 的禁用记录。
- 按当前要求，本版不包含“连续多局使用同一套路”的额外惩罚。

## 数据文件

状态保存在游戏用户数据目录中的：

```text
DynamicCardOdds/DynamicCardOdds.state.dat
```

替换 DLL 或重新安装模组不会要求重新设置；删除此文件才会重置统计与禁用记录。

## 兼容性

- Android v0.103.2
- Android v0.110.1
- PC v0.107.1
- 无 BaseLib 依赖

请从 GitHub Releases 下载与游戏平台及版本对应的中文文件名 ZIP。压缩包中的 `DynamicCardOdds` 文件夹应放入游戏 `mods` 目录。

## 构建

在 `src` 下运行：

```powershell
dotnet build DynamicCardOdds.csproj -c Release
```

可通过 `Sts2DataDir`、`Sts2Path` 和 `ModsPath` MSBuild 属性指定目标游戏程序集与输出位置。
