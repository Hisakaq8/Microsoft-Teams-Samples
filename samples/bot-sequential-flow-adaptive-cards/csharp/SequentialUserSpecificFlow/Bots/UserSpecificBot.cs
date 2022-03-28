﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using AdaptiveCards;
using AdaptiveCards.Templating;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Teams;
using Microsoft.Bot.Schema;
using Microsoft.Bot.Schema.Teams;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SequentialUserSpecificFlow.Helpers;
using SequentialUserSpecificFlow.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SequentialUserSpecificFlow.Bots
{
    public class UserSpecificBot : ActivityHandler
    {
        private List<Info> memberDetails = new List<Info> { };
        private readonly ConcurrentDictionary<string, List<IncidentDetails>> incidentDetailsList;
        private readonly ConcurrentDictionary<string, IsBotInstalled> isBotInstalled;

        public UserSpecificBot(ConcurrentDictionary<string, List<IncidentDetails>> _incidentDetailsList, ConcurrentDictionary<string, IsBotInstalled> _isBotInstalled)
        {
            incidentDetailsList = _incidentDetailsList;
            isBotInstalled = _isBotInstalled;
        }

        protected override async Task OnMessageActivityAsync(ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken)
        {
            string[] path = { ".", "Resources", "initialCard.json" };
            var member = await TeamsInfo.GetMemberAsync(turnContext, turnContext.Activity.From.Id, cancellationToken);
            var initialAdaptiveCard = GetFirstOptionsAdaptiveCard(path,turnContext.Activity.From.Name, member.Id);
            await turnContext.SendActivityAsync(MessageFactory.Attachment(initialAdaptiveCard), cancellationToken);
        }

        /// <summary>
        /// Invoked when members other than this bot (like a user) are removed from the conversation.
        /// </summary>
        /// <param name="turnContext">Context object containing information cached for a single turn of conversation with a user.</param>
        /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
        /// <returns>A task that represents the work queued to execute.</returns>
        protected override async Task OnConversationUpdateActivityAsync(ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken cancellationToken)
        {
            turnContext = turnContext ?? throw new ArgumentNullException(nameof(turnContext));
            if (turnContext.Activity.MembersAdded != null && turnContext.Activity.MembersAdded.Any(member => member.Id == turnContext.Activity.Recipient.Id))
            {
                var installBot = new IsBotInstalled()
                {
                    isBotInstalled = true
                };
                isBotInstalled.AddOrUpdate(turnContext.Activity.Conversation.Id, installBot, (key, value) => installBot);
                await turnContext.SendActivityAsync(MessageFactory.Text("Hello and Welcome"));
            }
            if (turnContext.Activity.MembersRemoved != null && turnContext.Activity.MembersRemoved.Any(member => member.Id == turnContext.Activity.Recipient.Id))
            {
                var installBot = new IsBotInstalled()
                {
                    isBotInstalled = false
                };
                isBotInstalled.AddOrUpdate(turnContext.Activity.Conversation.Id, installBot, (key, value) => installBot);
            }
        }

        protected override async Task<InvokeResponse> OnInvokeActivityAsync(ITurnContext<IInvokeActivity> turnContext, CancellationToken cancellationToken)
        {
            List<IncidentDetails> currentIncidentList = new List<IncidentDetails>();
            incidentDetailsList.TryGetValue("incidentList", out currentIncidentList);

            if (turnContext.Activity.Name == "composeExtension/submitAction")
            {
                var asJobject = JObject.FromObject(turnContext.Activity.Value);
                var data = (object)asJobject.ToObject<CardTaskFetchValue<object>>()?.Data;
                var botInstalled = (object)JObject.Parse(data.ToString()).ToObject<CardTaskFetchValue<object>>()?.MsTeams;

                if (botInstalled != null || !string.IsNullOrEmpty(botInstalled.ToString()))
                {
                    return GetIncientListFromMEAction(currentIncidentList);
                }
                else
                {
                    var incidentId = (string)JObject.Parse(data.ToString()).ToObject<CardTaskFetchValue<string>>()?.IncidentId;
                    var incidentDetail = currentIncidentList.FirstOrDefault(incident => incident.IncidentId.ToString() == incidentId);
                    await turnContext.SendActivityAsync(MessageFactory.Attachment(GetIncidentReviewCard(incidentDetail)));

                    return CreateInvokeResponse();
                }
            }

            if (turnContext.Activity.Name == "composeExtension/fetchTask")
            {
                var isBotInstalledInScope = new IsBotInstalled();
                isBotInstalled.TryGetValue(turnContext.Activity.Conversation.Id, out isBotInstalledInScope);

                if (isBotInstalledInScope == null || !isBotInstalledInScope.isBotInstalled)
                {
                    string[] paths = { ".", "Resources", "installBot.json" };
                    var adaptiveCardJson = File.ReadAllText(Path.Combine(paths));
                    var adaptiveCardAttachment = new Attachment()
                    {
                        ContentType = "application/vnd.microsoft.card.adaptive",
                        Content = JsonConvert.DeserializeObject(adaptiveCardJson),
                    };

                    return CreateInvokeResponse(new MessagingExtensionActionResponse
                    {
                        Task = new TaskModuleContinueResponse
                        {
                            Value = new TaskModuleTaskInfo
                            {
                                Card = adaptiveCardAttachment,
                                Height = 200,
                                Width = 400,
                                Title = "Bot is not installed",
                            },
                        },
                    });
                }
                else
                {
                    return GetIncientListFromMEAction(currentIncidentList);
                }
            }

            if (turnContext.Activity.Name == "adaptiveCard/action")
            {
                var data = JsonConvert.DeserializeObject<InitialSequentialCard>(turnContext.Activity.Value.ToString());
                string verb = data.action.verb;
                AdaptiveCardInvokeResponse adaptiveCardResponse;
                string cardJson;
                JObject response;
                switch (verb)
                {
                    case "initialRefresh":
                        string[] initialCard = { ".", "Resources", "firstCard.json" };
                        var members = new List<TeamsChannelAccount>();
                        string continuationToken = null;
                        do
                        {
                            var currentPage = await TeamsInfo.GetPagedMembersAsync(turnContext, 100, continuationToken, cancellationToken);
                            continuationToken = currentPage.ContinuationToken;
                            members.AddRange(currentPage.Members);
                        }
                        while (continuationToken != null);

                        foreach (var member in members)
                        {
                            if (member.AadObjectId != turnContext.Activity.From.AadObjectId)
                            {
                                var newMemberInfo = new Info { value = member.AadObjectId, title = member.Name };
                                memberDetails.Add(newMemberInfo);
                            }
                        }

                        adaptiveCardResponse = GetNextActionCard(initialCard, data);

                        return CreateInvokeResponse(adaptiveCardResponse);

                    case "firstCard":
                        string[] firstCard = { ".", "Resources", "secondCard.json" };
                        var assigneeInfo = await TeamsInfo.GetMemberAsync(turnContext, data.action.data.AssignedTo, cancellationToken);
                        data.action.data.UserMRI = assigneeInfo.Id;
                        adaptiveCardResponse = GetNextActionCard(firstCard, data);

                        return CreateInvokeResponse(adaptiveCardResponse);

                    case "secondCard":
                        string[] secondCard = { ".", "Resources", "thirdCard.json" };
                        if (data.action.data.Category == "Software")
                        {
                            adaptiveCardResponse = GetNextActionCard(secondCard, data, Constants.Software);
                        }
                        else
                        {
                            adaptiveCardResponse = GetNextActionCard(secondCard, data, Constants.Hardware);
                        }

                        return CreateInvokeResponse(adaptiveCardResponse);

                    case "thirdCard":
                        var initiator = await TeamsInfo.GetMemberAsync(turnContext, data.action.data.AssignedTo, cancellationToken);
                        data.action.data.AssignedToName = initiator.Name;
                        var incidentDetail = new IncidentDetails
                        {
                            IncidentId = Guid.NewGuid(),
                            CreatedBy = turnContext.Activity.From.Name,
                            AssignedToMRI = data.action.data.UserMRI,
                            AssignedToName = data.action.data.AssignedToName,
                            Category = data.action.data.Category,
                            IncidentTitle = data.action.data.IncidentTitle,
                            SubCategory = data.action.data.SubCategory
                        };
                        data.action.data.IncidentId = incidentDetail.IncidentId;
                        string[] thirdCard = { ".", "Resources", "reviewCard.json" };
                        var responseAttachment = GetResponseAttachment(thirdCard, data, out cardJson);
                        Activity pendingActivity = new Activity();
                        pendingActivity.Type = "message";
                        pendingActivity.Id = turnContext.Activity.ReplyToId;
                        pendingActivity.Attachments = new List<Attachment> { responseAttachment };
                        await turnContext.UpdateActivityAsync(pendingActivity);
                        response = JObject.Parse(cardJson);
                        adaptiveCardResponse = new AdaptiveCardInvokeResponse()
                        {
                            StatusCode = 200,
                            Type = "application/vnd.microsoft.card.adaptive",
                            Value = response
                        };
                      
                        if (currentIncidentList == null)
                        {
                            currentIncidentList = new List<IncidentDetails> { incidentDetail };
                        }
                        else
                        {
                            List<IncidentDetails> incidentList = new List<IncidentDetails>();
                            incidentList = currentIncidentList;
                            incidentList.Add(incidentDetail);
                            currentIncidentList = incidentList;
                        }
                        incidentDetailsList.AddOrUpdate("incidentList", currentIncidentList, (key, value) => currentIncidentList);
                        
                        return CreateInvokeResponse(adaptiveCardResponse);

                    case "refresh":

                        string[] assignedToCard = { ".", "Resources", "assignedToCard.json" };
                        adaptiveCardResponse = GetNextActionCard(assignedToCard, data);
                        
                        return CreateInvokeResponse(adaptiveCardResponse);

                    case "approved":

                        string[] approvedCard = { ".", "Resources", "approvedCard.json" };
                        var approvedAttachment = GetResponseAttachment(approvedCard, data, out cardJson);
                        Activity approvedActivity = new Activity();
                        approvedActivity.Type = "message";
                        approvedActivity.Id = turnContext.Activity.ReplyToId;
                        approvedActivity.Attachments = new List<Attachment> { approvedAttachment };
                        await turnContext.UpdateActivityAsync(approvedActivity);
                        response = JObject.Parse(cardJson);
                        adaptiveCardResponse = new AdaptiveCardInvokeResponse()
                        {
                            StatusCode = 200,
                            Type = "application/vnd.microsoft.card.adaptive",
                            Value = response
                        };
                        
                        return CreateInvokeResponse(adaptiveCardResponse);

                    case "rejected":

                        string[] rejectedCard = { ".", "Resources", "rejectedCard.json" };
                        var rejectedAttachment = GetResponseAttachment(rejectedCard, data, out cardJson);
                        Activity rejectedActivity = new Activity();
                        rejectedActivity.Type = "message";
                        rejectedActivity.Id = turnContext.Activity.ReplyToId;
                        rejectedActivity.Attachments = new List<Attachment> { rejectedAttachment };
                        await turnContext.UpdateActivityAsync(rejectedActivity);
                        response = JObject.Parse(cardJson);
                        adaptiveCardResponse = new AdaptiveCardInvokeResponse()
                        {
                            StatusCode = 200,
                            Type = "application/vnd.microsoft.card.adaptive",
                            Value = response
                        };
                        
                        return CreateInvokeResponse(adaptiveCardResponse);
                }
            }

            return null;
        }

        private Attachment GetFirstOptionsAdaptiveCard(string[] filepath, string name = null, string userMRI = null)
        {

            var adaptiveCardJson = File.ReadAllText(Path.Combine(filepath));
            AdaptiveCardTemplate template = new AdaptiveCardTemplate(adaptiveCardJson);
            var payloadData = new
            {
                createdById = userMRI,
                createdBy = name
            };
            var cardJsonstring = template.Expand(payloadData);
            var adaptiveCardAttachment = new Attachment()
            {
                ContentType = "application/vnd.microsoft.card.adaptive",
                Content = JsonConvert.DeserializeObject(cardJsonstring),
            };

            return adaptiveCardAttachment;
        }

        // Get incident list from messaging extension action command.
        public InvokeResponse GetIncientListFromMEAction(List<IncidentDetails> currentIncidentList)
        {
            if (currentIncidentList == null)
            {
                return CreateInvokeResponse(new MessagingExtensionActionResponse
                {
                    Task = new TaskModuleContinueResponse
                    {
                        Value = new TaskModuleTaskInfo
                        {
                            Card = GetNoInicidentFoundCard(),
                            Height = 200,
                            Width = 400,
                            Title = "No Incident found",
                        },
                    },
                });
            }
            else
            {
                var incidentList = new IncidentList();
                var listOfIncident = new List<IncidentChoiceSet>();
                foreach (var incident in currentIncidentList)
                {
                    var incidentdetail = new IncidentChoiceSet()
                    {
                        title = $"Incident title: {incident.IncidentTitle}, Created by: {incident.CreatedBy}",
                        value = incident.IncidentId
                    };
                    listOfIncident.Add(incidentdetail);
                }
                incidentList.incidentList = listOfIncident.ToArray();

                return CreateInvokeResponse(new MessagingExtensionActionResponse
                {
                    Task = new TaskModuleContinueResponse()
                    {
                        Value = new TaskModuleTaskInfo
                        {
                            Card = GetInicidentListCard(incidentList),
                            Height = 460,
                            Width = 600,
                            Title = "Incident list",
                        },
                    },
                });
            }
        }

        public AdaptiveCardInvokeResponse GetNextActionCard(string[] path, InitialSequentialCard data, List<string> subCategory = null)
        {
            var cardJson = File.ReadAllText(Path.Combine(path));
            AdaptiveCardTemplate template = new AdaptiveCardTemplate(cardJson);
            string subCat1 = "";
            string subCat2 = "";

            if (subCategory != null)
            {
                subCat1 = subCategory[0];
                subCat2 = subCategory[1];
            }

            var payloadData = new
            {
                assignees = memberDetails,
                incidentTitle = data.action.data.IncidentTitle,
                assignedTo = data.action.data.AssignedTo,
                category = data.action.data.Category,
                subCategory1 = subCat1,
                subCategory2 = subCat2,
                subCategory = data.action.data.SubCategory,
                createdBy = data.action.data.CreatedBy,
                assignedToName = data.action.data.AssignedToName,
                userMRI = data.action.data.UserMRI,
                incidentId = data.action.data.IncidentId
            };

            //"Expand" the template -this generates the final Adaptive Card payload
            var cardJsonstring = template.Expand(payloadData);
            var card = JObject.Parse(cardJsonstring);

            var adaptiveCardResponse = new AdaptiveCardInvokeResponse()
            {
                StatusCode = 200,
                Type = "application/vnd.microsoft.card.adaptive",
                Value = card
            };
            return adaptiveCardResponse;
        }

        private Attachment GetResponseAttachment(string[] filepath, InitialSequentialCard data, out string cardJsonString)
        {
            var adaptiveCardJson = File.ReadAllText(Path.Combine(filepath));
            AdaptiveCardTemplate template = new AdaptiveCardTemplate(adaptiveCardJson);
            var payloadData = new
            {
                incidentTitle = data.action.data.IncidentTitle,
                assignedTo = data.action.data.AssignedTo,
                category = data.action.data.Category,
                subCategory = data.action.data.SubCategory,
                createdBy = data.action.data.CreatedBy,
                assignedToName = data.action.data.AssignedToName,
                userMRI = data.action.data.UserMRI,
                incidentId = data.action.data.IncidentId
            };
            cardJsonString = template.Expand(payloadData);
            var adaptiveCardAttachment = new Attachment()
            {
                ContentType = "application/vnd.microsoft.card.adaptive",
                Content = JsonConvert.DeserializeObject(cardJsonString),
            };
            return adaptiveCardAttachment;
        }

        // Get no incident found card.
        private static Attachment GetNoInicidentFoundCard()
        {
            //Read the card json and create attachment.
            string[] paths = { ".", "Resources", "noIncidentFound.json" };
            var adaptiveCardJson = File.ReadAllText(Path.Combine(paths));

            var adaptiveCardAttachment = new Attachment()
            {
                ContentType = "application/vnd.microsoft.card.adaptive",
                Content = JsonConvert.DeserializeObject(adaptiveCardJson),
            };
            return adaptiveCardAttachment;
        }

        // Get incident list card.
        private static Attachment GetInicidentListCard(IncidentList incidentList)
        {
            //Read the card json and create attachment.
            string[] paths = { ".", "Resources", "incidentListCard.json" };
            var adaptiveCardJson = File.ReadAllText(Path.Combine(paths));
            AdaptiveCardTemplate template = new AdaptiveCardTemplate(adaptiveCardJson);
            var cardJsonstring = template.Expand(incidentList);
            var card = JObject.Parse(cardJsonstring);

            var adaptiveCardAttachment = new Attachment()
            {
                ContentType = "application/vnd.microsoft.card.adaptive",
                Content = card,
            };
            return adaptiveCardAttachment;
        }

        // Get incident review card send from messaging extension.
        private static Attachment GetIncidentReviewCard(IncidentDetails incidentDetail)
        {
            string[] paths = { ".", "Resources", "reviewCard.json" };
            var adaptiveCardJson = File.ReadAllText(Path.Combine(paths));
            AdaptiveCardTemplate template = new AdaptiveCardTemplate(adaptiveCardJson);
            var payloadData = new
            {
                incidentTitle = incidentDetail.IncidentTitle,
                assignedTo = incidentDetail.AssignedToName,
                category = incidentDetail.Category,
                subCategory = incidentDetail.SubCategory,
                createdBy = incidentDetail.CreatedBy,
                assignedToName = incidentDetail.AssignedToName,
                userMRI = incidentDetail.AssignedToMRI,
                incidentId = incidentDetail.IncidentId
            };
            var cardJsonString = template.Expand(payloadData);
            var adaptiveCardAttachment = new Attachment()
            {
                ContentType = "application/vnd.microsoft.card.adaptive",
                Content = JsonConvert.DeserializeObject(cardJsonString),
            };

            return adaptiveCardAttachment;
        }
    }
}