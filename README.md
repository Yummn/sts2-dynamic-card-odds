# 动态出率 / DynamicCardOdds

<!-- MOD-DOWNLOAD-ZH-START -->
## 中文下载与安装（先看这里）

- **动态出率**：请进入 [发布下载 / Releases](https://github.com/Yummn/sts2-dynamic-card-odds/releases)，展开 **Assets**，选择对应平台和游戏版本的中文安装包。
- **不要下载** Code → Download ZIP 或 Source code (zip/tar.gz)：那是源码，不含可直接安装的编译产物。
- 手动上传的源码压缩包已撤下，今后只发布安装包、说明和验证资料；项目代码仍保留在仓库。

### 本轮对应安装包

- [手机 v0.113.0](https://github.com/Yummn/sts2-dynamic-card-odds/releases/download/mobile-v113-pc-v111-20261117/DynamicCardOdds-1.0.7-Android-v113.0.zip)
- [电脑 v0.111.0（尚未实机回归）](https://github.com/Yummn/sts2-dynamic-card-odds/releases/download/mobile-v113-pc-v111-20261117/DynamicCardOdds-1.0.7-PC-v111.0.zip)

以上只是本轮构建目标，其他游戏版本请查看相应历史发布；不能混装手机和电脑版。旧文档中的版本号、源码附件指引如与本节冲突，以本节及对应Release说明为准。

### 电脑版安装

1. 退出游戏，解压**电脑版安装包**。
2. 将整个 `DynamicCardOdds` 文件夹放到游戏根目录的 `mods` 文件夹中；没有 `mods` 就新建。
3. 重启游戏，在模组列表中启用。不要只把ZIP或源码目录放在游戏根目录。

```text
Slay the Spire 2/mods/DynamicCardOdds/DynamicCardOdds.dll
Slay the Spire 2/mods/DynamicCardOdds/DynamicCardOdds.json
```

手机版请使用对应游戏版本的启动器导入安装包，不要拿电脑版代替；保留原有存档和模组配置。

GitHub 自动生成的 Source code 链接不能单独删除，因此请认准上面的中文安装包链接。
<!-- MOD-DOWNLOAD-ZH-END -->

> 项目分类：个人项目 / Slay the Spire 2 Mod / 可直接使用

《杀戮尖塔 2》故障机器人卡牌奖励学习模组。它会根据历史展示与选择情况动态调整同稀有度卡牌的奖励权重，并提供卡牌禁用功能。

它从 BetterDefect v0.11.35 起独立发布，不依赖 BetterDefect 或 BaseLib。

## 下载与安装

请从 [GitHub Releases](https://github.com/Yummn/sts2-dynamic-card-odds/releases) 下载与你的游戏平台和游戏版本对应的 ZIP。

手动安装时，将压缩包中的 `DynamicCardOdds` 文件夹完整复制到游戏 `mods` 目录。

## 兼容性

- Android v0.103.2
- Android v0.110.1
- PC v0.107.1
- 无 BaseLib 依赖

## 最新版本

- [v1.0.7](https://github.com/Yummn/sts2-dynamic-card-odds/releases/tag/v1.0.7)：修复手机百科大全禁用遮罩错位。遮罩现在挂在卡牌实际 CardContainer 下，并按容器中心坐标和实时尺寸绘制；已在 Android v110.1 实机完成禁用、遮罩对齐和重新启用验证。

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

## 构建

在 `src` 下运行：

```powershell
dotnet build DynamicCardOdds.csproj -c Release
```

可通过 `Sts2DataDir`、`Sts2Path` 和 `ModsPath` MSBuild 属性指定目标游戏程序集与输出位置。
