﻿// --------------------------------------------------------------------------
//  Copyright (c) Microsoft Corporation. All rights reserved.
//  Licensed under the MIT License.
// --------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Management.ApiManagement.ArmTemplates.Common.API.Clients.Abstractions;
using Microsoft.Azure.Management.ApiManagement.ArmTemplates.Common.Constants;
using Microsoft.Azure.Management.ApiManagement.ArmTemplates.Common.Exceptions;
using Microsoft.Azure.Management.ApiManagement.ArmTemplates.Common.Extensions;
using Microsoft.Azure.Management.ApiManagement.ArmTemplates.Common.Templates.Abstractions;
using Microsoft.Azure.Management.ApiManagement.ArmTemplates.Common.Templates.Apis;
using Microsoft.Azure.Management.ApiManagement.ArmTemplates.Common.Templates.Builders.Abstractions;
using Microsoft.Azure.Management.ApiManagement.ArmTemplates.Extractor.EntityExtractors.Abstractions;
using Microsoft.Azure.Management.ApiManagement.ArmTemplates.Extractor.Models;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.Management.ApiManagement.ArmTemplates.Extractor.EntityExtractors
{
    public class ApiExtractor : IApiExtractor
    {
        readonly ILogger<ApiExtractor> logger;
        readonly ITemplateBuilder templateBuilder;

        readonly IApisClient apisClient;

        readonly IDiagnosticExtractor diagnosticExtractor;
        readonly IApiSchemaExtractor apiSchemaExtractor;
        readonly IPolicyExtractor policyExtractor;
        readonly IProductApisExtractor productApisExtractor;
        readonly ITagExtractor tagExtractor;
        readonly IApiOperationExtractor apiOperationExtractor;
        readonly IApiRevisionClient apiRevisionClient;

        public ApiExtractor(
            ILogger<ApiExtractor> logger,
            ITemplateBuilder templateBuilder,
            IApisClient apisClient,
            IDiagnosticExtractor diagnosticExtractor,
            IApiSchemaExtractor apiSchemaExtractor,
            IPolicyExtractor policyExtractor,
            IProductApisExtractor productApisExtractor,
            ITagExtractor tagExtractor,
            IApiOperationExtractor apiOperationExtractor,
            IApiRevisionClient apiRevisionClient)
        {
            this.logger = logger;
            this.templateBuilder = templateBuilder;

            this.apisClient = apisClient;

            this.diagnosticExtractor = diagnosticExtractor;
            this.apiSchemaExtractor = apiSchemaExtractor;
            this.policyExtractor = policyExtractor;
            this.productApisExtractor = productApisExtractor;
            this.tagExtractor = tagExtractor;
            this.apiOperationExtractor = apiOperationExtractor;
            this.apiRevisionClient = apiRevisionClient;
        }

        public async Task<Template<ApiTemplateResources>> GenerateApiTemplateAsync(
            string singleApiName, 
            List<string> multipleApiNames, 
            string baseFilesGenerationDirectory, 
            ExtractorParameters extractorParameters)
        {
            var apiTemplate = this.templateBuilder
                                    .GenerateTemplateWithPresetProperties(extractorParameters)
                                    .Build<ApiTemplateResources>();

            if (!string.IsNullOrEmpty(singleApiName))
            {
                apiTemplate.TypedResources = await this.GenerateSingleApiTemplateResourcesAsync(singleApiName, baseFilesGenerationDirectory, extractorParameters);
            }
            else if (!multipleApiNames.IsNullOrEmpty())
            {
                apiTemplate.TypedResources = await this.GenerateMultipleApisTemplateAsync(multipleApiNames, baseFilesGenerationDirectory, extractorParameters);
            }
            else
            {
                var serviceApis = await this.apisClient.GetAllAsync(extractorParameters);
                this.logger.LogInformation("{0} APIs found ...", serviceApis.Count);

                var serviceApiNames = serviceApis.Select(api => api.Name).ToList();
                apiTemplate.TypedResources = await this.GenerateMultipleApisTemplateAsync(serviceApiNames, baseFilesGenerationDirectory, extractorParameters);
            }

            var serviceDiagnosticTemplateResources = await this.diagnosticExtractor.GetServiceDiagnosticsTemplateResourcesAsync(extractorParameters);
            if (!serviceDiagnosticTemplateResources.IsNullOrEmpty())
            {
                apiTemplate.TypedResources.Diagnostics.AddRange(serviceDiagnosticTemplateResources);
            }

            return apiTemplate;
        }

        public async Task<ApiTemplateResources> GenerateSingleApiTemplateResourcesAsync(string singleApiName, string baseFilesGenerationDirectory, ExtractorParameters extractorParameters, string apiDepends = null)
        {
            var apiTemplateResources = await this.GetApiRelatedTemplateResourcesAsync(singleApiName, baseFilesGenerationDirectory, extractorParameters);

            try
            {
                var apiResource = await this.apisClient.GetSingleAsync(singleApiName, extractorParameters);

                if (apiResource is null)
                {
                    throw new ServiceApiNotFoundException($"ServiceApi with name '{singleApiName}' not found");
                }

                this.logger.LogInformation("{0} API data found ...", singleApiName);
                this.SetArmTemplateValuesToApiTemplateResource(singleApiName, apiResource, extractorParameters, apiDepends);
                this.SanitizeOutputApiTemplateResource(singleApiName, apiResource, apiTemplateResources);
                apiTemplateResources.Apis.Add(apiResource);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, $"Exception occured while generating service api template for {singleApiName}.");
                throw;
            }

            return apiTemplateResources;
        }

        async Task<ApiTemplateResources> GenerateMultipleApisTemplateAsync(List<string> multipleApiNames, string baseFilesGenerationDirectory, ExtractorParameters extractorParameters)
        {
            var generalApiTemplateResourcesStorage = new ApiTemplateResources();

            var apiDependencies = await this.GenerateApiRevisionsDependenciesAsync(extractorParameters);

            foreach (var apiName in multipleApiNames)
            {
                apiDependencies.TryGetValue(apiName, out var apiDependsOn);

                var specificApiTemplateResources = await this.GenerateSingleApiTemplateResourcesAsync(apiName, baseFilesGenerationDirectory, extractorParameters, apiDependsOn);
                generalApiTemplateResourcesStorage.AddResourcesData(specificApiTemplateResources);
            }

            return generalApiTemplateResourcesStorage;
        }

        void SetArmTemplateValuesToApiTemplateResource(string apiName, ApiTemplateResource apiResource, ExtractorParameters extractorParameters, string apiDepends = null)
        {
            apiResource.Name = $"[concat(parameters('{ParameterNames.ApimServiceName}'), '/{apiName}')]";
            apiResource.ApiVersion = GlobalConstants.ApiVersion;
            apiResource.Scale = null;
            apiResource.DependsOn = Array.Empty<string>();
            var apiResourceDepends = new List<string>();

            if (!string.IsNullOrEmpty(apiDepends)) {
                apiResourceDepends.Add(NamingHelper.GenerateApisResourceId(apiDepends));
            }

            if (extractorParameters.ParameterizeServiceUrl)
            {
                apiResource.Properties.ServiceUrl = $"[parameters('{ParameterNames.ServiceUrl}').{NamingHelper.GenerateValidParameterName(apiName, ParameterPrefix.Api)}]";
            }

            if (extractorParameters.ParametrizeApiOauth2Scope)
            {
                if (apiResource.Properties.AuthenticationSettings?.OAuth2?.Scope is not null)
                {
                    apiResource.Properties.AuthenticationSettings.OAuth2.Scope = $"[parameters('{ParameterNames.ApiOauth2ScopeSettings}').{NamingHelper.GenerateValidParameterName(apiName, ParameterPrefix.ApiOauth2Scope)}]";
                }
            }

            if (apiResource.Properties.ApiVersionSetId != null)
            {
                string versionSetName = apiResource.Properties.ApiVersionSetId;
                int versionSetPosition = versionSetName.IndexOf("apiVersionSets/");

                versionSetName = versionSetName.Substring(versionSetPosition, versionSetName.Length - versionSetPosition);
                apiResource.Properties.ApiVersionSetId = $"[concat(resourceId('Microsoft.ApiManagement/service', parameters('{ParameterNames.ApimServiceName}')), '/{versionSetName}')]";
            }

            apiResource.DependsOn = apiResourceDepends.ToArray();
        }

        /// <summary>
        /// Adds related API Template resources like schemas, operations, products, tags etc.
        /// </summary>
        /// <param name="apiName">The name of the API.</param>
        /// <param name="extractorParameters">The extractor.</param>
        /// <returns></returns>
        public async Task<ApiTemplateResources> GetApiRelatedTemplateResourcesAsync(string apiName, string baseFilesGenerationDirectory, ExtractorParameters extractorParameters)
        {
            var apiTemplateResources = new ApiTemplateResources();

            var apiSchemaResources = await this.apiSchemaExtractor.GenerateApiSchemaResourcesAsync(apiName, extractorParameters);
            if (apiSchemaResources?.ApiSchemas.IsNullOrEmpty() == false)
            {
                apiTemplateResources.ApiSchemas = apiSchemaResources.ApiSchemas;
            }

            var apiDiagnosticResources = await this.diagnosticExtractor.GetApiDiagnosticsResourcesAsync(apiName, extractorParameters);
            if (!apiDiagnosticResources.IsNullOrEmpty())
            {
                apiTemplateResources.Diagnostics = apiDiagnosticResources;
            }

            var apiPolicyResource = await this.policyExtractor.GenerateApiPolicyResourceAsync(apiName, baseFilesGenerationDirectory, extractorParameters);
            if (apiPolicyResource is not null)
            {
                apiTemplateResources.ApiPolicies.Add(apiPolicyResource);
            }

            var apiProducts = await this.productApisExtractor.GenerateSingleApiTemplateAsync(apiName, extractorParameters, addDependsOnParameter: true);
            if (!apiProducts.IsNullOrEmpty())
            {
                apiTemplateResources.ApiProducts = apiProducts;
            }

            var apiTags = await this.tagExtractor.GenerateTagResourcesLinkedToApiAsync(apiName, extractorParameters);
            if (!apiTags.IsNullOrEmpty())
            {
                apiTemplateResources.Tags = apiTags;
            }

            var apiOperations = await this.apiOperationExtractor.GenerateApiOperationsResourcesAsync(apiName, extractorParameters);
            if (!apiOperations.IsNullOrEmpty())
            {
                apiTemplateResources.ApiOperations = apiOperations;
            }

            foreach (var apiOperation in apiOperations)
            {
                var apiOperationPolicy = await this.policyExtractor.GenerateApiOperationPolicyResourceAsync(apiName, apiOperation.OriginalName, baseFilesGenerationDirectory, extractorParameters);
                if (apiOperationPolicy is not null)
                {
                    apiTemplateResources.ApiOperationsPolicies.Add(apiOperationPolicy);
                }

                var apiOperationTags = await this.tagExtractor.GenerateTagResourcesLinkedToApiOperationAsync(apiName, apiOperation.OriginalName, extractorParameters);
                if (!apiOperationTags.IsNullOrEmpty())
                {
                    apiTemplateResources.ApiOperationsTags.AddRange(apiOperationTags);
                }
            }

            return apiTemplateResources;
        }

        /// <summary>
        /// Generates dictionary with api and api revisions dependency. 
        /// </summary>
        /// <returns></returns>
        async Task<Dictionary<string, string>> GenerateApiRevisionsDependenciesAsync(ExtractorParameters extractorParameters)
        {
            var apiDependency = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(extractorParameters.SingleApiName) || !extractorParameters.MultipleApiNames.IsNullOrEmpty())
            {
                return apiDependency;
            }

            var apis = await this.apisClient.GetAllCurrentAsync(extractorParameters);

            if (apis.IsNullOrEmpty())
            {
                this.logger.LogWarning($"No current apis were found for '{extractorParameters.SourceApimName}' at '{extractorParameters.ResourceGroup}'");
                return apiDependency;
            }

            foreach (var api in apis)
            {
                string apiName = api.OriginalName;
                var apiRevisions = await this.apiRevisionClient.GetApiRevisionsAsync(apiName, extractorParameters);

                foreach (var apiRevision in apiRevisions)
                {
                    if (apiRevision.IsCurrent)
                    {
                        continue;
                    }

                    apiDependency.Add(apiRevision.ApiId.Split("/")[2], api.Name);
                }
            }

            return apiDependency;
        }

        void SanitizeOutputApiTemplateResource(string apiName, ApiTemplateResource apiResource, ApiTemplateResources apiTemplateResources)
        {
            //remove api operation if websocket type & change dependencies to api, since api operation for websocket type is created automatically and it is not allowed to update it (built-in operation)
            if (apiResource.Properties.Type == ApiTypeConstants.WebSocket)
            {
                if (!apiTemplateResources.ApiOperations.IsNullOrEmpty())
                {
                    if (!apiTemplateResources.ApiOperationsPolicies.IsNullOrEmpty())
                    {
                        foreach (var apiOperationPolicy in apiTemplateResources.ApiOperationsPolicies)
                        {
                            apiOperationPolicy.DependsOn = new string[] { NamingHelper.GenerateApisResourceId(apiName) };
                        }
                    }

                    if (!apiTemplateResources.ApiOperationsTags.IsNullOrEmpty())
                    {
                        foreach (var apiOperationTag in apiTemplateResources.ApiOperationsTags)
                        {
                            apiOperationTag.DependsOn = new string[] { NamingHelper.GenerateApisResourceId(apiName) };
                        }
                    }
                    apiTemplateResources.ApiOperations.Clear();
                }
            }
        }
    }
}
