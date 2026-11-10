# Core Temp 温度桥接

原版恢复脚本 Test.au3 17957 从 Main/CoreTemp_x64 取得组件路径，22597、22619、22666 向缺失的压力测试包装器传递此路径。提供目录中的实际组件为 Other/Tools/CoreTemp/CoreTemp_x64.exe 1.17.1.0；原包装器温度读取实现没有恢复，不能称下面的代码是其原始实现。

新实现以 Core Temp 官方共享内存协议为互操作依据：`CoreTempMappingObject`，固定结构前缀 2688 字节，依次为 256 个负载值、128 个 TjMax、每 CPU 核心数、CPU 数、256 个 float 温度、4 个 float 频率/电压参数、100 字节 CPU 名以及 Fahrenheit/DeltaToTjMax 标志。协议参考来自 https://www.alcpu.com/CoreTemp/developers.html 的 CoreTempSDK.zip，已留存于 artifacts/verification/CoreTempSDK-reference。SDK DLL 不参与构建或发布。

`CoreTempReader.Read()` 每次重新打开只读共享内存；没有映射、无权限、结构无效、没有有效读数均返回 null，不缓存上轮数值、不填入 0℃。摄氏温度采用有效核心最高值，支持华氏和距 TjMax 的值转换。该最高值是新宿主明确采用的聚合方式，不冒充已确认的原包装器聚合算法。

`CoreTempSession.Start(run, report)` 在真实测试期间管理可选温度提供器。已存在有效映射时复用；发现已有 Core Temp 进程而映射无效时不启动另一实例，也不关闭用户实例。组件从 ReferenceRoot/Other/Tools/CoreTemp 或发布目录 Tools 下查找，复制到本轮唯一临时目录。私有 INI 关闭更新、插件、独立日志和过热动作；源目录中的配置保持不变。进程由本任务 Job Object 管理，释放 session 时只结束本轮进程并清理自己的临时目录。

TelemetryCollector 将真实读数写入原列顺序的 GW_CoreTemp.log；演练不启动提供器，温度为 Unavailable。温度组件退出或不支持当前 CPU 时也保持 Unavailable，不能据此改变压力引擎 PASS/FAIL。

软件验证：CoreTempTests 的两项测试覆盖有效结构、最高温度、多种无效值、华氏和 TjMax 转换，均通过。未启动实际传感器组件，因此本记录不宣称当前电脑或其他型号已获取到温度；组件自身的驱动/CPU 支持仍需目标机验证。
