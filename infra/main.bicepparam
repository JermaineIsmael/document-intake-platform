using './main.bicep'

param env = 'dev'
param authAuthority = 'https://login.microsoftonline.com/<tenant-id>/v2.0'
param authAudience = '<api-client-id>'
param bootstrap = true
