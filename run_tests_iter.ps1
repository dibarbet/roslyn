function RunTest
{
    Write-Host "Running..."
    Remove-Item C:\cores\*
    dotnet test .\artifacts\bin\Microsoft.CodeAnalysis.EditorFeatures.UnitTests\Debug\net472\Microsoft.CodeAnalysis.EditorFeatures.UnitTests.dll --filter "FullyQualifiedName=Microsoft.CodeAnalysis.Editor.UnitTests.CommentSelection.ToggleBlockCommentCommandHandlerTests.Throws" --blame
    $childItems = Get-ChildItem C:\cores -Name -Include *.dmp
    $HasDump = $false
    if ($childItems)
    {
        $HasDump = $childItems.Contains("testhost.net472")
    }
    if (Get-Process -Name "WerFault.exe" -ea SilentlyContinue)
    {
        Write-Host "Wer RUNNING!!!!"
        $HasDump = $true
    }

    Remove-Item C:\cores\*
    Write-Host "Completed with $HasDump"
    return $HasDump
}

$count = 0
1..10 | ForEach-Object {
    $result = RunTest
    Write-Host "Result $result"
    if ($result -eq $true)
    {
        $count++
    }
}
Write-Host "Count: $count"