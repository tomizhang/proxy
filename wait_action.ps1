param($runId)
$headers = @{ "User-Agent" = "PowerShell" }
$url = "https://api.github.com/repos/tomizhang/proxy/actions/runs/$runId"

Write-Host "Monitoring GitHub Actions Run $runId..."
$timeout = [DateTime]::UtcNow.AddMinutes(20)

while ([DateTime]::UtcNow -lt $timeout) {
    Start-Sleep -Seconds 15
    try {
        $run = Invoke-RestMethod -Uri $url -Headers $headers
        $status = $run.status
        $conclusion = $run.conclusion
        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Status: $status, Conclusion: $conclusion"
        if ($status -eq "completed") {
            Write-Host "FINAL_CONCLUSION: $conclusion"
            if ($conclusion -eq "success") {
                exit 0
            } else {
                exit 1
            }
        }
    } catch {
        Write-Host "Warning: $($_.Exception.Message)"
    }
}
Write-Host "Timeout reached!"
exit 2
