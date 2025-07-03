set version=1.01
set dotnetcore=net5.0

rem Root solution folder
cd /d "C:\Users\Jan\Source\repos\WatchCalendar"


rem Delete Builds folder
del /f /q /s Builds\*.* > nul
rmdir /q /s Builds
mkdir Builds


rem Delete bin folder of WatchCalendar
del /f /q /s Source\WatchCalendar\bin\*.* > nul
rmdir /q /s Source\WatchCalendar\bin

rem .NET Core 5
dotnet publish Source\WatchCalendar\WatchCalendar.csproj /p:PublishProfile=Source\WatchCalendar\Properties\PublishProfiles\linux-arm.pubxml --configuration Release
dotnet publish Source\WatchCalendar\WatchCalendar.csproj /p:PublishProfile=Source\WatchCalendar\Properties\PublishProfiles\linux-x64.pubxml --configuration Release
dotnet publish Source\WatchCalendar\WatchCalendar.csproj /p:PublishProfile=Source\WatchCalendar\Properties\PublishProfiles\osx-x64.pubxml --configuration Release
dotnet publish Source\WatchCalendar\WatchCalendar.csproj /p:PublishProfile=Source\WatchCalendar\Properties\PublishProfiles\win-x64.pubxml --configuration Release
dotnet publish Source\WatchCalendar\WatchCalendar.csproj /p:PublishProfile=Source\WatchCalendar\Properties\PublishProfiles\win-x86.pubxml --configuration Release

rem .NET Core 3.1
dotnet publish Source\WatchCalendar\WatchCalendar.csproj /p:PublishProfile=Source\WatchCalendar\Properties\PublishProfiles\linux-arm-netcoreapp3.1.pubxml --configuration Release
rem Workaround: Somehow Settings.ini does not get copied to publish folder, only to output folder?!
copy Source\WatchCalendar\bin\Release\netcoreapp3.1\linux-arm\Settings.ini Source\WatchCalendar\bin\Release\netcoreapp3.1\publish\linux-arm\

rem Rar all WatchCalendar builds
rem .NET Core 5
cd /d "C:\Users\Jan\Source\repos\WatchCalendar"
cd Source\WatchCalendar\bin\Release\net5.0\publish\linux-arm\
"C:\Program Files\WinRAR\rar.exe" a -r ..\..\..\..\..\..\..\Builds\WatchCalendar-%version%-linux-arm-%dotnetcore%.rar *.*

cd /d "C:\Users\Jan\Source\repos\WatchCalendar"
cd Source\WatchCalendar\bin\Release\net5.0\publish\linux-x64\
"C:\Program Files\WinRAR\rar.exe" a -r ..\..\..\..\..\..\..\Builds\WatchCalendar-%version%-linux-x64-%dotnetcore%.rar *.*

cd /d "C:\Users\Jan\Source\repos\WatchCalendar"
cd Source\WatchCalendar\bin\Release\net5.0\publish\osx-x64\
"C:\Program Files\WinRAR\rar.exe" a -r ..\..\..\..\..\..\..\Builds\WatchCalendar-%version%-osx-x64-%dotnetcore%.rar *.*

cd /d "C:\Users\Jan\Source\repos\WatchCalendar"
cd Source\WatchCalendar\bin\Release\net5.0\publish\win-x64\
"C:\Program Files\WinRAR\rar.exe" a -r ..\..\..\..\..\..\..\Builds\WatchCalendar-%version%-win-x64-%dotnetcore%.rar *.*

cd /d "C:\Users\Jan\Source\repos\WatchCalendar"
cd Source\WatchCalendar\bin\Release\net5.0\publish\win-x86\
"C:\Program Files\WinRAR\rar.exe" a -r ..\..\..\..\..\..\..\Builds\WatchCalendar-%version%-win-x86-%dotnetcore%.rar *.*

rem Rar all WatchCalendar builds
rem .NET Core 3.1
cd /d "C:\Users\Jan\Source\repos\WatchCalendar"
cd Source\WatchCalendar\bin\Release\netcoreapp3.1\publish\linux-arm\
"C:\Program Files\WinRAR\rar.exe" a -r ..\..\..\..\..\..\..\Builds\WatchCalendar-%version%-linux-arm-netcoreapp3.1.rar *.*


cd /d "C:\Users\Jan\Source\repos\WatchCalendar"
