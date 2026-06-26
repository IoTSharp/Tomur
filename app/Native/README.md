# Native 托管边界

本目录只放 C# 侧 native runtime 访问边界，包括动态库解析、`NativeLibrary.Load`、P/Invoke 声明、backend 探测和面向上层服务的托管适配。

不在本目录放置 C++ 源码、CMake 工程、已编译动态库或模型权重。native backend 源码与打包边界统一放在仓库根级 `native/` 目录，运行时释放与校验目标统一放在 Tomur 管理的数据目录。
