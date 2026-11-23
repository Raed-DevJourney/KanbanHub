// A monthly budget on the resource group (specification section 18.4): it stops nothing, but it writes to the
// people named when 80% is spent and when the month is forecast to go over.

import { costBudget } from '../types.bicep'

param env string
param budget costBudget

resource monthly 'Microsoft.Consumption/budgets@2023-11-01' = {
  name: 'budget-kh-${env}'
  properties: {
    category: 'Cost'
    amount: budget.amount
    timeGrain: 'Monthly'
    timePeriod: { startDate: budget.startDate }
    notifications: {
      spent80Percent: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 80
        thresholdType: 'Actual'
        contactEmails: budget.contactEmails
      }
      forecastOver: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        thresholdType: 'Forecasted'
        contactEmails: budget.contactEmails
      }
    }
  }
}
