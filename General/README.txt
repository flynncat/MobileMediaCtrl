MediaBrowser - 绿色版便携执行文件
==========================================

本目录用于存放 build.ps1 打包出来的最终可执行文件 MediaBrowser.exe。

【如何生成】
    在仓库根目录运行：
        powershell -ExecutionPolicy Bypass -File .\build.ps1
    脚本完成后，本目录会出现 MediaBrowser.exe。

【如何使用】
    - MediaBrowser.exe 是完全独立的单文件可执行程序，
      包含 .NET 8 运行时与全部依赖；
    - 可以直接复制到任意 Windows 10/11 (x64) 电脑上双击运行，
      无需安装 .NET、无需任何环境配置；
    - 用户配置（语言、开机自启动等）保存在
      %LocalAppData%\MediaBrowser\settings.json。

【注意】
    本目录的 *.exe 等打包产物不纳入 git 版本控制，
    仅保留 README.txt 与 .gitkeep 占位文件。
