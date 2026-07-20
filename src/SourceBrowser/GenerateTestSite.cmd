@echo off
rem Two-pass demo: /force windows pass then /incremental linux pass, both indexing the
rem same two "repos" (TestCode\TestSolution.sln = RepoA, TestCode\RepoB\RepoB.slnx = RepoB).
rem This registers 2 configs on the generated site, so the config selector (folded into
rem the code view -- see TestCode\TestSolution\PlatformInfo.cs / TestCode\RepoB\RepoBLib\
rem PlatformInfo.cs for the divergent files) and the multi-repo grouping in Solution
rem Explorer are both visible once RunTestSite.cmd is run against the output.
rem
rem /out is src\SourceIndexServer\TestSite -- a dedicated, disposable folder (gitignored),
rem deliberately NOT src\SourceIndexServer itself (a /force run deletes /out wholesale, and
rem that folder is the project's source tree) and NOT nested under HtmlGenerator's own
rem bin\...\Web folder (WebsiteFinalizer.Finalize copies that "Web" template into /out on
rem every pass, so nesting /out inside it would re-copy the site into itself each run).
rem RunTestSite.cmd points SourceIndexServer's --contentRoot at this same TestSite folder.
if exist src\SourceIndexServer\TestSite rd /s /q src\SourceIndexServer\TestSite
src\HtmlGenerator\bin\Debug\net10.0\HtmlGenerator.exe TestCode\TestSolution.sln TestCode\RepoB\RepoB.slnx /out:src\SourceIndexServer\TestSite /force /config:windows /configAxes:os=windows /p:DefineConstants=WINDOWS /repoPath:"TestCode\RepoB"="RepoB" /repoPath:"TestCode"="RepoA"
src\HtmlGenerator\bin\Debug\net10.0\HtmlGenerator.exe TestCode\TestSolution.sln TestCode\RepoB\RepoB.slnx /out:src\SourceIndexServer\TestSite /incremental /config:linux /configAxes:os=linux /repoPath:"TestCode\RepoB"="RepoB" /repoPath:"TestCode"="RepoA"