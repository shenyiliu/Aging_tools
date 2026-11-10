# REA 复刻与组件复用记录

基准：用户提供的 GW_TEST 5.0.0818；本轮开发版本 0.2.0。用户明确要求复用现有组件，因此在独立 Tools 目录复制，不写回原安装。原版默认配置在此前获授权的真实短测中曾被引擎写回，详情仍保留于《真实短测结果_20261110》，不声称原目录从未发生修改。

## REA 本轮证据

- 原 Test.exe 的 PE32 资源完整清单，SHA256 `7ab11db3e62f89e7a838a9fb4fe1c5086a8be24a2bc98885bce6da309918c1bd`；17 项资源，包含名为 SCRIPT 的 RCDATA。Evidence `ev_ac788ef49526bedeab65d67d032d03266df6682f08be899143c3876b73fdb6c5`。
- 原 ControlPanel.exe 的 PE 资源完整清单保存于研究目录 `evidence/rea-round2-ControlPanel-resources.json`。
- 原 bit.exe 的 PE32+ AMD64 导入清单，SHA256 `9c789820c63e0f8d9e7b7af7f343f326cc657ff76d65f9e56d09ca160979c9f6`；Evidence `ev_9c52cf6ceef1c641d4b11838fa007a658c4eff7b6871df247a6bdad0fafd2728`。
- 三条完整 Evidence 导出为 `GW_TEST_逆向研究/evidence/rea-round2-bundle.json`。静态资源/导入不证明动态功能；业务复刻还依赖已保存的独立 AutoIt 恢复脚本，逐项来源在《原版老化行为对照_20261110》。

## 复用清单

`scripts/copy-reference-tools.ps1` 创建 Tools，保留 BurnIn 完整引擎及辅助资源、Sleep/PwrTest、Reboot/cpufan、SFT/视频、CoreTemp、ICON、电源语言以及 3DMark/FurMark 现有定义；每个复制文件校验 SHA256。共 312 个原文件，清单 `Tools/reuse-manifest.json`；另生成只含老化设置的 Other/Config.ini。

原 Test.exe、Reboot/Sleep/ControlPanel/ViewLOG 主包装器没有当作新宿主直接执行。订单、OA3、出货封装、旧运行日志和卸载器不作为老化组件复制。新安装自动定位本安装的 Tools；字节相同的旧参考引擎路径可迁移到自身副本，独立自选覆盖路径保留。

原包不存在 BurnInTest.exe/FurMarkTest.exe/3DMarkTest.exe 三个压力包装器；FurMark/3DMark 可执行引擎也没有。复制现有文件不能补出缺失实现或建立逐像素窗口依据。BurnIn 包装器已按原接口重写，其余缺失引擎不能被标为可运行。

## 真实证据与限制

独立 WMP 静音三秒探测已观察到播放状态 3 和进度前进；自动 FAIL 结束，不能当作人工画面/声音通过。隔离 BurnIn 加载所选配置后退出码 0，无 RUN 指令，未运行压力；开发副本默认配置哈希不变。CoreTemp 原旧版本启动十秒无有效温度，软件仍显示未获取。

55 项 Core、17 项 Windows 软件测试通过。项目窗/视频计数/下一次开机/风扇解析与准备取消有软件回归，但没有把这些称为真实电源或厂商 EC 的实机验收。本轮未执行真实重启、睡眠、风扇/EC 写入、最大音量切换或第二轮完整 BurnIn 压力。

设置仍保留 Tools 路径和适配扩展页，属于新的管理入口；原版订单/OA3 设置不在老化范围。跨型号、DPI、引擎全部模式、停止/崩溃、恢复及长稳仍需验证；当前版本不是已经验收的完全 1:1 成品。
