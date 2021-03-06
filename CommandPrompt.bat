@CD /D "%~dp0"
@title ZipDeploy Command Prompt
@SET PATH=C:\Program Files\dotnet\;%PATH%
type readme.md
@doskey bc=dotnet clean
@doskey btw=dotnet watch msbuild /p:FilterTest="test =~ $1" /p:NoCoverage=true $2 $3 $4 $5 $6 $7 $8 $9
@doskey bt=dotnet msbuild /p:FilterTest="test =~ $1" /p:NoCoverage=true $2 $3 $4 $5 $6 $7 $8 $9
@doskey bw=dotnet watch msbuild /p:FilterTest="cat != Slow" $*
@doskey ba=dotnet msbuild $*
@doskey b=dotnet msbuild /p:FilterTest="cat != Slow" $*
@doskey br=dotnet restore $*
@echo.
@echo Aliases:
@echo.
@doskey /MACROS
@CD Build
%comspec%