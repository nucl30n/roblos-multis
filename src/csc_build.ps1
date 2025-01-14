dotnet publish app.csproj -c Release -o ../bin --self-contained -r win-x64 /p:PublishSingleFile=true /p:DebugType=None /p:IncludeAllContentForSelfExtract=true
Compress-Archive -Path ../bin/RoblosMultis.exe -DestinationPath ../bin/RoblosMultis.zip -Force
