# Basic integration sample

Three scripts that show the whole integration. They need the Firebase Remote Config, AppLovin MAX and Metica SDKs in the project, so import this sample only into a game that has them.

| File | What it shows |
|---|---|
| `MeticaAssignmentStartup.cs` | `Initialize` at app start, the optional `EventLogged` copy for your analytics, `SetConsentStatus` |
| `MeticaAssignmentRemoteConfig.cs` | Reporting the Firebase fetch result with `ReportRemoteConfig` |
| `MeticaAssignmentAds.cs` | Choosing the route with `Current.RunMetica`, the Metica arm, the MAX (Baseline) arm, `ReportAdsInit`, `TagPlacement` |

Replace the `YOUR_...` values with your own. Call `MeticaAssignmentRemoteConfig.Fetch()` once Firebase is ready and `MeticaAssignmentAds.InitializeAsync()` when the game starts ads. The full steps are in the repository README.
