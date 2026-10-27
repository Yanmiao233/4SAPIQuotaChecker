param([string]$Root)

# 一体化 E2E：旧版实例已在运行的前提下
#   打开设置 -> 检查更新 -> 等待对话框 -> 下载(最长35分钟) -> 重启并更新 -> 验证版本

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$ErrorActionPreference = 'Stop'

$AEi = [System.Windows.Automation.AutomationElement]
$TS  = [System.Windows.Automation.TreeScope]
$CT  = [System.Windows.Automation.ControlType]

function FindBtn($scope, $name) {
    $c = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AEi::ControlTypeProperty, $CT::Button)),
        (New-Object System.Windows.Automation.PropertyCondition($AEi::NameProperty, $name)))
    $scope.FindFirst($TS::Descendants, $c)
}
function DialogTexts() {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $all = $root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AEi::IsControlElementProperty, $true)))
    @($all | ForEach-Object { $_.Current.Name })
}

$proc = Get-Process -Name QuotaChecker -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Output 'NO_APP'; exit 1 }
$oldVer = (Get-Item $proc.Path).VersionInfo.FileVersion
Write-Output "old: pid=$($proc.Id) ver=$oldVer"

$win = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst($TS::Children,
    (New-Object System.Windows.Automation.PropertyCondition($AEi::ProcessIdProperty, [int]$proc.Id)))
(FindBtn $win '设置').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 2500

$chk = FindBtn ([System.Windows.Automation.AutomationElement]::RootElement) '检查更新'
if (-not $chk) { Write-Output 'FAIL: no check button'; exit 1 }
$chk.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Output 'checking…'

# 等待出现「发现新版本」或「已是最新」（最长 90 秒）
$state = $null
for ($i = 0; $i -lt 18; $i++) {
    Start-Sleep -Seconds 5
    $texts = DialogTexts
    if ($texts | Where-Object { $_ -eq '发现新版本 v1.4.0' }) { $state = 'available'; break }
    if ($texts | Where-Object { $_ -like '已是最新版本*' })      { $state = 'uptodate'; break }
    if ($texts | Where-Object { $_ -like '检查更新失败*' })      { $state = 'error'; break }
}
Write-Output "check state: $state"
if ($state -ne 'available') {
    if ($state -eq 'error') { (DialogTexts | Where-Object { $_ -like '检查更新失败*' } | Select-Object -First 1) }
    exit 1
}

(FindBtn ([System.Windows.Automation.AutomationElement]::RootElement) '下载并安装').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Output 'downloading (最长 35 分钟)…'

# 等主按钮变「重启并更新」
$btn = $null
for ($i = 0; $i -lt 350; $i++) {
    Start-Sleep -Seconds 6
    if (-not (Get-Process -Name QuotaChecker -ErrorAction SilentlyContinue)) { Write-Output "APP DIED ~$(($i+1)*6)s"; exit 1 }
    $btn = FindBtn ([System.Windows.Automation.AutomationElement]::RootElement) '重启并更新'
    if ($btn) { Write-Output "ready ~$(($i+1)*6)s"; break }
}
if (-not $btn) { Write-Output 'TIMEOUT'; exit 1 }

$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Output 'installing…'

# 等新版本进程出现（更新器最多等 30s 退出 + 替换 + 重启）
$ok = $false
for ($j = 0; $j -lt 40; $j++) {
    Start-Sleep -Seconds 2
    $a = Get-Process -Name QuotaChecker -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($a) {
        $fver = (Get-Item $a.Path).VersionInfo.FileVersion
        if ($fver -eq '1.4.0.0') {
            Write-Output "SUCCESS: restarted v1.4.0 (~$((($i+1)*6)+$j*2)s) path=$($a.Path)"
            $ok = $true
            break
        }
    }
}
if (-not $ok) { Write-Output 'FAIL: no v1.4.0 restart'; if (Test-Path "$env:LOCALAPPDATA\QuotaChecker\crash.log") { Get-Content "$env:LOCALAPPDATA\QuotaChecker\crash.log" -Raw } }
