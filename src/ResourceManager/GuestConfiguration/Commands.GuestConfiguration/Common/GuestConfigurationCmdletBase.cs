﻿// ----------------------------------------------------------------------------------
//
// Copyright Microsoft Corporation
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// http://www.apache.org/licenses/LICENSE-2.0
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// ----------------------------------------------------------------------------------

namespace Microsoft.Azure.Commands.GuestConfiguration.Common
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using Newtonsoft.Json.Linq;

    using Microsoft.Azure.Commands.Common.Authentication;
    using Microsoft.Azure.Commands.Common.Authentication.Abstractions;
    using Microsoft.Azure.Commands.ResourceManager.Common;
    using Microsoft.Azure.Commands.GuestConfiguration.Helpers;
    using Microsoft.Azure.Commands.GuestConfiguration.Models;
    using Microsoft.Azure.Management.GuestConfiguration;
    using Microsoft.Azure.Management.GuestConfiguration.Models;
    using Microsoft.Azure.Management.ResourceManager;
    using Microsoft.Azure.Management.ResourceManager.Models;
    using ErrorResponseException = Management.ResourceManager.Models.ErrorResponseException;
    using GuestConfigurationErrorResponseException = Management.GuestConfiguration.Models.ErrorResponseException;

    /// <summary>
    /// Base class for Azure Guest configuration cmdlets
    /// </summary>
    public abstract class GuestConfigurationCmdletBase : AzureRMCmdlet
    {
        /// <summary>
        /// Guest configuration client
        /// </summary>
        private IGuestConfigurationClient _guestConfigurationClient;

        /// <summary>
        /// Azure policy client
        /// </summary>
        private IPolicyClient  _policyClient;

        private string _initiativeId = null;

        /// <summary>
        /// Gets or sets the guest configuration client
        /// </summary>
        public IGuestConfigurationClient GuestConfigurationClient
        {
            get
            {
                return _guestConfigurationClient ?? 
                    (_guestConfigurationClient = AzureSession.Instance.ClientFactory.CreateArmClient<GuestConfigurationClient>(DefaultContext, AzureEnvironment.Endpoint.ResourceManager));
            }
        }

        /// <summary>
        /// Gets or sets the Policy client
        /// </summary>
        public IPolicyClient PolicyClient
        {
            get
            {
                return _policyClient ??
                    (_policyClient = AzureSession.Instance.ClientFactory.CreateArmClient<PolicyClient>(DefaultContext, AzureEnvironment.Endpoint.ResourceManager));
            }
        }

        // Get all guest configuration policy assignments for a VM
        protected IEnumerable<GuestConfigurationPolicyAssignment> GetAllGuestConfigurationAssignments(string resourceGroupName, string vmName)
        {

            var gcPolicyAssignmentsList = new List<GuestConfigurationPolicyAssignment>();
            var gcPolicySetDefinitions = GetAllGuestConfigPolicySetDefinitions();

            if (gcPolicySetDefinitions == null || gcPolicySetDefinitions.Count() == 0)
            {
                return null;
            }

            foreach (var gcPolicySetDefinition in gcPolicySetDefinitions)
            {
                var gcAssignments = GetAllGuestConfigurationAssignmentsByInitiativeId(resourceGroupName, vmName, gcPolicySetDefinition.Id);
                if(gcAssignments != null || gcAssignments.Count() > 0)
                {
                    gcPolicyAssignmentsList.AddRange(gcAssignments);
                }
            }
            return gcPolicyAssignmentsList;
        }

        // Get all guest configuration policy assignment reports for a VM
        protected IEnumerable<GuestConfigurationPolicyAssignmentReport> GetAllGuestConfigurationAssignmentReports(string resourceGroupName, string vmName)
        {
            var gcPolicyAssignmentReportList = new List<GuestConfigurationPolicyAssignmentReport>();
            var gcPolicySetDefinitions = GetAllGuestConfigPolicySetDefinitions();

            if (gcPolicySetDefinitions == null || gcPolicySetDefinitions.Count() == 0)
            {
                return null;
            }

            foreach (var gcPolicySetDefinition in gcPolicySetDefinitions)
            {
                var gcAssignmentReports = GetAllGuestConfigurationAssignmentReportsByInitiativeId(resourceGroupName, vmName, gcPolicySetDefinition.Id);
                if (gcAssignmentReports != null || gcAssignmentReports.Count() > 0)
                {
                    gcPolicyAssignmentReportList.AddRange(gcAssignmentReports);
                }
            }
            return gcPolicyAssignmentReportList;
        }

        // Get guest configuration policy assignments by initiative definition name
        protected IEnumerable<GuestConfigurationPolicyAssignment> GetAllGuestConfigurationAssignmentsByInitiativeName(string resourceGroupName, string vmName, string initiativeName)
        {

            PolicySetDefinition policySetDefinition;
            // Get policy set definition (initiative)
            try
            {
                policySetDefinition = PolicyClient.PolicySetDefinitions.GetBuiltIn(initiativeName);
            }
            catch(ErrorResponseException exception) when (HttpStatusCode.NotFound.Equals(exception.Response.StatusCode))
            {
                var message = string.IsNullOrEmpty(this._initiativeId) ?
                string.Format(Resources.InitiativeWithThisNameNotFound, initiativeName) :
                string.Format(Resources.InitiativeWithThisIdNotFound, this._initiativeId);
                throw new ErrorResponseException(message);
            }

            var category = GetPolicySetCategory(policySetDefinition);

            if (Constants.GuestConfigurationCategory.ToLower() != category.ToLower())
            {
                var message = string.IsNullOrEmpty(this._initiativeId) ?
                    string.Format(Resources.InitiativeWithThisNameNotOfCategoryGuestConfiguration, initiativeName) :
                    string.Format(Resources.InitiativeWithThisIdNotOfCategoryGuestConfiguration, this._initiativeId);
                throw new ErrorResponseException(message);
            }

            // Get policy definitions in initiative
            var policyDefinitionIdsInInitiative = policySetDefinition.PolicyDefinitions.Select(
                policyDef =>
                {
                    return policyDef.PolicyDefinitionId;
                }
            ).ToList();

            // Get all policy definitions in subscription
            // var policyDefinitions = PolicyClient.PolicyDefinitions.List().ToList();
            var policyDefinitionsForTheInitiative = new List<PolicyDefinition>();

            foreach(var policyDefinitionIdInInitiative in policyDefinitionIdsInInitiative)
            {
                var _initiativeName = GetInitiativeNameFromId(policyDefinitionIdInInitiative);
                var policyDef = PolicyClient.PolicyDefinitions.GetBuiltIn(_initiativeName);
                if(policyDef != null)
                {
                    policyDefinitionsForTheInitiative.Add(policyDef);
                }
            }

            var gcPolicyAssignmentsList = new List<GuestConfigurationPolicyAssignment>();

            IEnumerable<GuestConfigurationAssignment> gcrpAssignments = null;
            try
            {
                gcrpAssignments = GuestConfigurationClient.GuestConfigurationAssignments.List(resourceGroupName, vmName);
            }
            catch (GuestConfigurationErrorResponseException exception) when (HttpStatusCode.NotFound.Equals(exception.Response.StatusCode))
            {
                this.WriteVerbose(string.Format(Resources.InvalidRGOrVMName, resourceGroupName, vmName));
                throw exception;
            }

            var gcrp_AssignmentName_Assignment_Map = new Dictionary<string, GuestConfigurationAssignment>();
            foreach(var gcrpAssignment in gcrpAssignments)
            {
                gcrp_AssignmentName_Assignment_Map.Add(gcrpAssignment.Name, gcrpAssignment);
            }

            // Get all gcrp assignments for the initiative - for policy definitions  of category "Guest Configuration", effectType "AuditIfNotExists"   
            foreach (var policyDef in policyDefinitionsForTheInitiative)
            {
                var policyRule = JObject.Parse(policyDef.PolicyRule.ToString());
                var policyRuleDictionary = policyRule.ToObject<Dictionary<string, object>>();

                var policyRuleThen = JObject.Parse(policyRuleDictionary["then"].ToString());
                var policyRuleThenDictionary = policyRuleThen.ToObject<Dictionary<string, object>>();
                var effectType = policyRuleThenDictionary["effect"].ToString();

                if (Constants.AuditIfNotExists.ToLower() != effectType.ToLower())
                {
                    continue;
                }

                var policyRuleThenDetails = JObject.Parse(policyRuleThenDictionary["details"].ToString());
                var policyRuleDetailsDictionary = policyRuleThenDetails.ToObject<Dictionary<string, object>>();

                if(Constants.GuestConfigurationRPType.ToLower() != policyRuleDetailsDictionary["type"].ToString().ToLower())
                {
                    continue;
                }

                var guestConfigurationAssignmentNameInPolicy = policyRuleDetailsDictionary["name"].ToString();

                if (!string.IsNullOrEmpty(guestConfigurationAssignmentNameInPolicy) && gcrp_AssignmentName_Assignment_Map.ContainsKey(guestConfigurationAssignmentNameInPolicy))
                {
                    var gcrpAsgnment = gcrp_AssignmentName_Assignment_Map[guestConfigurationAssignmentNameInPolicy];
                    if(gcrpAsgnment != null)
                    {
                        gcPolicyAssignmentsList.Add(new GuestConfigurationPolicyAssignment(gcrpAsgnment, policyDef.DisplayName));
                    }                         
                }
            }

            // sort by policy display name
            gcPolicyAssignmentsList.Sort(new GuestConfigurationPolicyAssignmentPolicyDisplayNameComparer());
            return gcPolicyAssignmentsList;
        }

        // Get guest configuration policy assignments by initiative definition name
        protected IEnumerable<GuestConfigurationPolicyAssignment> GetAllGuestConfigurationAssignmentsByInitiativeId(string resourceGroupName, string vmName, string initiativeId)
        {
            this._initiativeId = initiativeId;
            var initiativeName = GetInitiativeNameFromId(initiativeId);          
            var gcPolicyAssignments = GetAllGuestConfigurationAssignmentsByInitiativeName(resourceGroupName, vmName, initiativeName);

            return gcPolicyAssignments;
        }

        // Get guest configuration policy assignment reports by initiative definition name
        protected IEnumerable<GuestConfigurationPolicyAssignmentReport> GetAllGuestConfigurationAssignmentReportsByInitiativeName(string resourceGroupName, string vmName, string initiativeName)
        {
            var gcPolicyAssignments = GetAllGuestConfigurationAssignmentsByInitiativeName(resourceGroupName, vmName, initiativeName);
            var gcPolicyAssignmentReportList = new List<GuestConfigurationPolicyAssignmentReport>();

            foreach (var gcPolicyAssignment in gcPolicyAssignments)
            {
                var reportGuid = CommonHelpers.GetReportGUIDFromID(gcPolicyAssignment.LatestReportId);
                var gcrpReport = GuestConfigurationClient.GuestConfigurationAssignmentReports.Get(resourceGroupName, gcPolicyAssignment.Configuration.Name, reportGuid, vmName);
                gcPolicyAssignmentReportList.Add(new GuestConfigurationPolicyAssignmentReport(gcrpReport, gcPolicyAssignment));  
            }

            // sort gcPolicyAssignmentReportList by Policy display name, end time 
            gcPolicyAssignmentReportList.Sort(new GuestConfigurationPolicyAssignmentReportEndDateComparer());
            return gcPolicyAssignmentReportList;
        }

        // Get guest configuration policy assignment reports by initiative definition name
        protected IEnumerable<GuestConfigurationPolicyAssignmentReport> GetAllGuestConfigurationAssignmentReportsByInitiativeId(string resourceGroupName, string vmName, string initiativeId)
        {
            var initiativeName = GetInitiativeNameFromId(initiativeId);
            var gcPolicyAssignmentReportList = GetAllGuestConfigurationAssignmentReportsByInitiativeName(resourceGroupName, vmName, initiativeName);
            return gcPolicyAssignmentReportList;
        }

        // Get guest configuration policy assignment report by reportId
        protected GuestConfigurationPolicyAssignmentReport GetGuestConfigurationAssignmentReportById(string reportId)
        {

            var urlParameters = CommonHelpers.GetGCURLParameters(reportId);
            var reportGuid = CommonHelpers.GetReportGUIDFromID(reportId);
            
            if(urlParameters == null || reportGuid == null)
            {
                throw new ErrorResponseException(string.Format(Resources.InvalidReportId, reportId));
            }

            GuestConfigurationPolicyAssignmentReport policyReport = null;
            if (urlParameters != null && urlParameters.AreParametersNotNullOrEmpty() && !string.IsNullOrEmpty(reportId))
            {
                GuestConfigurationAssignmentReport report = null;
                try
                {
                    report = GuestConfigurationClient.GuestConfigurationAssignmentReports.Get(urlParameters.ResourceGroupName, urlParameters.AssignmentName, reportGuid, urlParameters.VMName);
                }
                catch (GuestConfigurationErrorResponseException exception) when (HttpStatusCode.NotFound.Equals(exception.Response.StatusCode))
                {
                    this.WriteVerbose(string.Format(Resources.NotFoundByReportId, reportId));
                    throw exception;
                }
                policyReport = new GuestConfigurationPolicyAssignmentReport(report, null);
            }
            return policyReport;
        }

        private string GetInitiativeNameFromId(string initiativeId)
        {
            var indexOfInitiativeName = initiativeId.LastIndexOf("/");
            if (indexOfInitiativeName < 0 || indexOfInitiativeName == initiativeId.Length - 1)
            {
                throw new ErrorResponseException(string.Format(Resources.NoInitiativeNameFound, initiativeId));
            }
            var initiativeName = initiativeId.Substring(indexOfInitiativeName + 1);
            return initiativeName;
        }

        private string GetPolicySetCategory(PolicySetDefinition initiativeDefinition)
        {
            var categoryMetadata = JObject.Parse(initiativeDefinition.Metadata.ToString());
            var categoryMetadataDictionary = categoryMetadata.ToObject<Dictionary<string, object>>();
            var category = categoryMetadataDictionary["category"].ToString();
            return category;
        }

        private IEnumerable<PolicySetDefinition> GetAllGuestConfigPolicySetDefinitions()
        {
            var policySetDefinitions = PolicyClient.PolicySetDefinitions.ListBuiltIn();
            if (policySetDefinitions == null || policySetDefinitions.Count() == 0)
            {
                return null;
            }

            var gcPolicySetDefinitions = policySetDefinitions.Where(policySetDef =>
            {
                var category = GetPolicySetCategory(policySetDef);
                return Constants.GuestConfigurationCategory.ToLower() == category.ToLower();
            });
            return gcPolicySetDefinitions;
        }

        public class GuestConfigurationPolicyAssignmentPolicyDisplayNameComparer : IComparer<GuestConfigurationPolicyAssignment>
        {
            public int Compare(GuestConfigurationPolicyAssignment first, GuestConfigurationPolicyAssignment second)
            {
                if(first != null && !string.IsNullOrEmpty(first.PolicyDisplayName) && second != null && !string.IsNullOrEmpty(second.PolicyDisplayName))
                {
                    return first.PolicyDisplayName.CompareTo(second.PolicyDisplayName);
                }
                return 0;
            }
        }

        public class GuestConfigurationPolicyAssignmentReportEndDateComparer : IComparer<GuestConfigurationPolicyAssignmentReport>
        {
            public int Compare(GuestConfigurationPolicyAssignmentReport first, GuestConfigurationPolicyAssignmentReport second)
            {
                if (first != null && first.EndTime != null && second != null && second.EndTime != null)
                {
                    var firstDate = Convert.ToDateTime(first.EndTime);
                    var secondDate = Convert.ToDateTime(second.EndTime);

                    if (firstDate != null && secondDate != null && firstDate >= secondDate)
                    {
                        return -1;
                    }
                    return 0;
                }
                return 0;
            }
        }
    }
}