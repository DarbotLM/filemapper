@{
    RootModule = 'FileMapper.PowerShell.dll'
    ModuleVersion = '0.3.11'
    GUID = '5c656205-c8ae-461c-aaf0-8b36552b4e8b'
    Author = 'FileMapper'
    Description = 'Native typed cmdlets for the authenticated local FileMapper service.'
    PowerShellVersion = '7.6'
    CompatiblePSEditions = @('Core')
    CmdletsToExport = @('Start-FileMapperContentAnalysis','Get-FileMapperContentAnalysis','Suspend-FileMapperContentAnalysis','Resume-FileMapperContentAnalysis','Stop-FileMapperContentAnalysis','Get-FileMapperContentMatch','Get-FileMapperContentMember','Get-FileMapperContentOverlap','Get-FileMapperContentEvidence','Get-FileMapperContentChunk', 'Get-FileMapperContentPath','Start-FileMapperScan','Suspend-FileMapperScan','Resume-FileMapperScan','Stop-FileMapperScan','Start-FileMapperAnalysis','Get-FileMapperSnapshot','Get-FileMapperJob','Get-FileMapperItem','Get-FileMapperTree','Get-FileMapperRanking','Get-FileMapperContext','Get-FileMapperBreakdown','Find-FileMapperDuplicate','Get-FileMapperDuplicateMember','Find-FileMapperRoute','Invoke-FileMapperAudit','Get-FileMapperRobocopyCatalog','Test-FileMapperRobocopyDraft','New-FileMapperRobocopyPlan','Get-FileMapperRobocopyPlan','Start-FileMapperRobocopyPreview','Get-FileMapperRobocopyJob','Get-FileMapperRobocopyEvent','Get-FileMapperRobocopyOutput','Stop-FileMapperRobocopyJob','Import-FileMapperRobocopyJob','Export-FileMapperRobocopyJob','Get-FileMapperSemanticObject','Compare-FileMapperSemanticSnapshot','Get-FileMapperSemanticChallenge','Invoke-FileMapperSemanticReplay','Export-FileMapperSemanticSnapshot','Get-FileMapperLearner','New-FileMapperLearner','Get-FileMapperLearningVocabulary','Get-FileMapperLearningSubject','Get-FileMapperLearningPartition','Invoke-FileMapperLearningObservation','Invoke-FileMapperLearningEvent','Invoke-FileMapperLearningPrediction','Get-FileMapperLearningOccurrence','Get-FileMapperArena','New-FileMapperArena','Invoke-FileMapperArenaReplay','Invoke-FileMapperArenaComputer','Save-FileMapperArenaReplay')
    FunctionsToExport = @()
    AliasesToExport = @()
    VariablesToExport = @()
    FormatsToProcess = @('FileMapper.format.ps1xml')
    PrivateData = @{ PSData = @{ Tags = @('FileMapper','Filesystem','Graph') } }
}
