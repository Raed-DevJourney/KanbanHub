// Shapes shared by main.bicep and its modules.

@export()
@description('A model to deploy in Azure OpenAI. Names, versions and what a region offers change: take them from the model catalog.')
type modelDeployment = {
  @description('The model, for example a small chat model or text-embedding-3-small.')
  name: string
  version: string

  @description('How the deployment is billed and routed, for example GlobalStandard or Standard.')
  sku: string

  @description('Thousands of tokens per minute the deployment may use.')
  capacity: int
}

@export()
@description('A monthly spending alert for the resource group (specification section 18.4). It warns; it stops nothing.')
type costBudget = {
  @description('The monthly amount in the subscription\'s billing currency.')
  amount: int

  @description('Who is written to at 80% spent and when the month is forecast to go over.')
  contactEmails: string[]

  @description('The first day of the month the budget is created in, as yyyy-MM-01. Azure does not let it change afterwards.')
  startDate: string
}

@export()
@description('The GitHub App of this environment (specification section 9.1). None of this is secret; the secrets live in Key Vault.')
type gitHubApp = {
  @description('The app\'s client id.')
  clientId: string

  @description('The login the app acts under, for example kanbanhub-dev[bot].')
  botLogin: string

  @description('The Entra object id the app acts as inside KanbanHub.')
  principalId: string
}
