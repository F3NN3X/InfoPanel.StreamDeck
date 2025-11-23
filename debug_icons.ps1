
$appData = [Environment]::GetFolderPath("ApplicationData")
$profilesDir = Join-Path $appData "Elgato\StreamDeck\ProfilesV2"

Write-Host "Profiles Dir: $profilesDir"

$profiles = Get-ChildItem $profilesDir -Directory

foreach ($prof in $profiles) {
    $profilePath = $prof.FullName
    Write-Host "Checking Profile: $($prof.Name)"
    
    $profilesSubDir = Join-Path $profilePath "Profiles"
    if (Test-Path $profilesSubDir) {
        $pages = Get-ChildItem $profilesSubDir -Directory
        foreach ($page in $pages) {
            $manifestPath = Join-Path $page.FullName "manifest.json"
            if (Test-Path $manifestPath) {
                try {
                    $json = Get-Content $manifestPath -Raw | ConvertFrom-Json
                    if ($json.Controllers) {
                        foreach ($controller in $json.Controllers) {
                            if ($controller.Actions) {
                                $actions = $controller.Actions
                                foreach ($key in $actions.PSObject.Properties.Name) {
                                    $action = $actions.$key
                                    $stateIdx = $action.State
                                    if ($action.States -and $action.States.Count -gt $stateIdx) {
                                        $state = $action.States[$stateIdx]
                                        $image = $state.Image
                                        $title = $state.Title
                                        
                                        if ($image) {
                                            Write-Host "  Found Image for Key $key : $image"
                                            
                                            # Check Page Dir
                                            $fullPathPage = Join-Path $page.FullName $image
                                            if (Test-Path $fullPathPage) {
                                                Write-Host "    [OK] Found in Page Dir: $fullPathPage"
                                            }
                                            else {
                                                Write-Host "    [MISSING] Not in Page Dir: $fullPathPage"
                                                
                                                # Check Profile Dir
                                                $fullPathProfile = Join-Path $profilePath $image
                                                if (Test-Path $fullPathProfile) {
                                                    Write-Host "    [OK] Found in Profile Dir: $fullPathProfile"
                                                }
                                                else {
                                                    Write-Host "    [MISSING] Not in Profile Dir: $fullPathProfile"
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch {
                    Write-Host "  Error parsing manifest: $_"
                }
            }
        }
    }
}
