using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Net;
using System.Text;
using System.Net.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Mvc;
using viewer.Hubs;
using viewer.Models;
using Azure;
using Azure.Communication.Messages;

namespace viewer.Controllers
{
    [Route("api/[controller]")]
    public class UpdatesController : Controller
    {
        #region Data Members

        private bool EventTypeSubcriptionValidation
            => HttpContext.Request.Headers["aeg-event-type"].FirstOrDefault() ==
               "SubscriptionValidation";

        private bool EventTypeNotification
            => HttpContext.Request.Headers["aeg-event-type"].FirstOrDefault() ==
               "Notification";

        private readonly IHubContext<GridEventsHub> _hubContext;

        #endregion

        #region Constructors

        public UpdatesController(IHubContext<GridEventsHub> gridEventsHubContext)
        {
            this._hubContext = gridEventsHubContext;
        }

        #endregion

        #region Public Methods

        [HttpOptions]
        public async Task<IActionResult> Options()
        {
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            {
                var webhookRequestOrigin = HttpContext.Request.Headers["WebHook-Request-Origin"].FirstOrDefault();
                var webhookRequestCallback = HttpContext.Request.Headers["WebHook-Request-Callback"];
                var webhookRequestRate = HttpContext.Request.Headers["WebHook-Request-Rate"];
                HttpContext.Response.Headers.Add("WebHook-Allowed-Rate", "*");
                HttpContext.Response.Headers.Add("WebHook-Allowed-Origin", webhookRequestOrigin);
            }

            return Ok();
        }

        [HttpPost]
        public async Task<IActionResult> Post()
        {
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            {
                var jsonContent = await reader.ReadToEndAsync();

                // Check the event type.
                // Return the validation code if it's 
                // a subscription validation request. 
                if (EventTypeSubcriptionValidation)
                {
                    return await HandleValidation(jsonContent);
                }
                else if (EventTypeNotification)
                {
                    // Check to see if this is passed in using
                    // the CloudEvents schema
                    if (IsCloudEvent(jsonContent))
                    {
                        return await HandleCloudEvent(jsonContent);
                    }

                    return await HandleGridEvents(jsonContent);
                }

                return BadRequest();                
            }
        }

        #endregion

        #region Private Methods

        private async Task<JsonResult> HandleValidation(string jsonContent)
        {
            var gridEvent =
                JsonConvert.DeserializeObject<List<GridEvent<Dictionary<string, string>>>>(jsonContent)
                    .First();

            await this._hubContext.Clients.All.SendAsync(
                "gridupdate",
                gridEvent.Id,
                gridEvent.EventType,
                gridEvent.Subject,
                gridEvent.EventTime.ToLongTimeString(),
                jsonContent.ToString());

            //await this.replyMessage(jsonContent, 1);

            // Retrieve the validation code and echo back.
            var validationCode = gridEvent.Data["validationCode"];
            return new JsonResult(new
            {
                validationResponse = validationCode
            });
        }

        private async Task<IActionResult> HandleGridEvents(string jsonContent)
        {
            var events = JArray.Parse(jsonContent);
            foreach (var e in events)
            {
                // Invoke a method on the clients for 
                // an event grid notiification.                        
                var details = JsonConvert.DeserializeObject<GridEvent<CommunicationEventData>>(e.ToString());
                await this._hubContext.Clients.All.SendAsync(
                    "gridupdate",
                    details.Id,
                    details.EventType,
                    details.Subject,
                    details.EventTime.ToLongTimeString(),
                    e.ToString());

                try {
                    await this.replyMessage(details, jsonContent);
                }
                catch (Exception eee) {
                    Console.WriteLine($"e: {eee}");
                }
            }


            return Ok();
        }

        private async Task<IActionResult> HandleCloudEvent(string jsonContent)
        {
            var details = JsonConvert.DeserializeObject<CloudEvent<dynamic>>(jsonContent);
            var eventData = JObject.Parse(jsonContent);

            await this._hubContext.Clients.All.SendAsync(
                "gridupdate",
                details.Id,
                details.Type,
                details.Subject,
                details.Time,
                eventData.ToString()
            );

            //await this.replyMessage(jsonContent, 3);

            return Ok();
        }

        public class CommunicationEventData
        {
            public string Content { get; set; }
            public string ChannelType { get; set; }
            public string From { get; set; }
            public string To { get; set; }
            public DateTime ReceivedTimestamp { get; set; }
        }

        private async Task replyMessage(GridEvent<CommunicationEventData> detail, string jsonContent)
        {
            Console.WriteLine("Azure Communication Services - Send WhatsApp Messages\n");
            Console.WriteLine($"jsonContent: {jsonContent}");


            if (detail.EventType.Contains("AdvancedMessageReceived"))
            {
                string connectionString = "endpoint=https://gientech-whatsapp-communication-services.unitedstates.communication.azure.com/;accesskey=16NWai2al6q3WJNa2FFazyBfJaP/fYR3cnf6uGaP/jkf1/wRKR1HOh7Yc0JTtTLNnB4Y6jfrZ9oClLLCnc950A==";
                NotificationMessagesClient notificationMessagesClient =
                    new NotificationMessagesClient(connectionString);

                string channelRegistrationId = "ddfeab03-844a-4bfb-8935-b3013c065944";


                var reply = "I am still in training. Could you please rephrase your question? or input \"Hi\" to start again";

                if (detail.Data.Content == "Hi")
                {
                    reply = "Welcome to the GienTech POC! To initiate a conversation, kindly provide the mobile OTP.";
                }
                else
                {
                    if (detail.Data.Content.Length == 4 && detail.Data.Content != "9900" && int.TryParse(detail.Data.Content, out int number))
                    {
                        reply = "The OTP entered is invalid. Please try again.";
                    }
                    else
                    {
                        reply = "Hello! I'm here to assist you. How can I help you today?";
                    }
                }


                var recipient = new List<string> { detail.Data.From };
                var textContent = new TextNotificationContent(new Guid(channelRegistrationId), recipient, reply);

                SendMessageResult result = await notificationMessagesClient.SendAsync(textContent);

                Console.WriteLine($"Message id: {result.Receipts[0].MessageId}");
            }
        }

        private static bool IsCloudEvent(string jsonContent)
        {
            // Cloud events are sent one at a time, while Grid events
            // are sent in an array. As a result, the JObject.Parse will 
            // fail for Grid events. 
            try
            {
                // Attempt to read one JSON object. 
                var eventData = JObject.Parse(jsonContent);

                // Check for the spec version property.
                var version = eventData["specversion"].Value<string>();
                if (!string.IsNullOrEmpty(version)) return true;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }

            return false;
        }

        #endregion
    }
}