# SecAgent 联动

ICC-CE 侧插件，为 SecAgent 连接插件提供普通 HTTP JSON 服务。它不实现 MCP，也不修改 SecAgent 工作区配置。

插件启动后监听 `127.0.0.1:18790`，提供 `/health`、`/tools` 和 `/tools/{name}` 接口，保留 ICC-CE 版本、设置路径、设置读取和安全更新工具。

插件会在设置页检测当前 CE 是否包含原生 SVG 插入适配。检测到旧版 CE 缺少画布插入、选中、擦除、清空或撤销适配时，设置页会显示不可用原因，并且 HTTP 工具目录不会暴露 `insert_iccce_svg`。

当前版本使用 WPF 原生矢量元素承载可编辑 SVG 场景，不依赖浏览器渲染；支持插入后的整体选中、移动、等比例缩放、局部擦除和撤销。

SecAgent 端请安装 `ICC-CE-SecAgent-Connector`；连接插件会自动重试并在服务可用时注册工具和 Skill。
