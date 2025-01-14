dotnet publish app.csproj -c Release -o ../bin -r win-x64 /p:PublishSingleFile=true /p:DebugType=None --no-self-contained
#Compress-Archive -Path ../bin/RoblosMultis.exe -DestinationPath bin/RoblosMultis.zip -Force
