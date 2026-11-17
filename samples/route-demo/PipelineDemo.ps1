# Analysis fixture only. Do not execute with untrusted input.
param([string]$UntrustedExpression)
function Show-Label {
    param([string]$Label)
    $Label.Trim() | Write-Output
}
$UntrustedExpression | Invoke-Expression
