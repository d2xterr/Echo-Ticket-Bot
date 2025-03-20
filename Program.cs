using System;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DiscordTicketBotConsole
{
    class Program
    {
        // Define constants and variables
        private static DiscordSocketClient _client;
        private static Dictionary<ulong, DateTime> _lastTicketCreation = new Dictionary<ulong, DateTime>();
        private const string HELPER_ROLE_NAME = "Helper";
        private const string MOD_ROLE_NAME = "Moderator";
        private static int _totalTickets = 0;
        private const string TOKEN_FILE = "bot_token.txt";
        private const string TICKETS_LOG_FILE = "tickets.txt";
        private const ulong LOG_CHANNEL_ID = LOG_CHANNEL_ID; // Updated log channel ID
        private const ulong TICKET_CHANNEL_ID = TICKET_CHANNEL_ID; // Channel ID for ticket dropdown
        private static bool _adminPanelSent = false; // Flag to track if admin panel has been sent

        static async Task Main(string[] args)
        {
            Console.Title = "Discord Ticket Bot";
            Console.WriteLine("=== Discord Ticket Bot ===");
            Console.WriteLine("This bot creates ticket channels using a dropdown menu");

            await InitializeBotAsync();

            // Graceful shutdown
            Console.CancelKeyPress += async (sender, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("Shutting down the bot...");
                await _client.StopAsync();
                Environment.Exit(0);
            };

            // Keep the console app running
            while (true)
            {
                await Task.Delay(1000);
            }
        }

        private static Task LogAsync(LogMessage log)
        {
            Console.WriteLine(log.ToString());
            return Task.CompletedTask;
        }

        private static async Task InitializeBotAsync()
        {
            var config = new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.DirectMessages |
                                 GatewayIntents.Guilds |
                                 GatewayIntents.GuildMessages |
                                 GatewayIntents.GuildMessageReactions,
                LogLevel = LogSeverity.Info
            };

            _client = new DiscordSocketClient(config);
            _client.Log += LogAsync;
            _client.Ready += ReadyAsync;
            _client.Disconnected += OnDisconnectedAsync;
            _client.MessageReceived += MessageReceivedAsync;
            _client.InteractionCreated += InteractionCreatedAsync;

            string token = GetTokenFromFileOrPrompt();
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Bot token cannot be empty");

            Console.WriteLine("Connecting to Discord...");
            await _client.LoginAsync(TokenType.Bot, token);
            await _client.StartAsync();
        }

        private static string GetTokenFromFileOrPrompt()
        {
            if (File.Exists(TOKEN_FILE))
            {
                return File.ReadAllText(TOKEN_FILE).Trim();
            }
            Console.Write("Enter your Discord bot token: ");
            string token = Console.ReadLine()?.Trim();
            if (!string.IsNullOrWhiteSpace(token)) File.WriteAllText(TOKEN_FILE, token);
            return token;
        }

        private static async Task MessageReceivedAsync(SocketMessage message)
        {
            if (message.Author.IsBot) return;

            // Handle direct messages
            if (message.Channel is SocketDMChannel)
            {
                await HandleDirectMessageAsync(message);
            }

            // Log messages sent in ticket channels
            if (message.Channel is SocketTextChannel textChannel && textChannel.Name.StartsWith("ticket-"))
            {
                LogTicketMessage(message);
            }
        }

        private static async Task HandleDirectMessageAsync(SocketMessage message)
        {
            var user = message.Author;

            // Check if the user is on cooldown
            if (_lastTicketCreation.TryGetValue(user.Id, out var lastCreationTime) && (DateTime.Now - lastCreationTime).TotalMinutes < 1)
            {
                var timeRemaining = TimeSpan.FromMinutes(1) - (DateTime.Now - lastCreationTime);
                await user.SendMessageAsync($"You can only create one ticket every 1 minute. Please wait {timeRemaining.Seconds} seconds.");
                return;
            }

            // Create a dropdown menu for ticket creation
            var selectMenu = new SelectMenuBuilder()
                .WithCustomId("ticket_reason")
                .WithPlaceholder("Select a reason for your ticket")
                .AddOption("Report a Player", "report", "Report rule-breaking or toxic behavior.")
                .AddOption("Won a Giveaway", "giveaway", "Claim a prize from a giveaway.")
                .AddOption("Get Support by Staff", "support", "Get help from our support team.");

            var component = new ComponentBuilder()
                .WithSelectMenu(selectMenu)
                .Build();

            var embed = new EmbedBuilder()
                .WithTitle("Echo Tickets")
                .WithDescription("Please select a reason for creating a ticket from the dropdown menu below.")
                .AddField("Report a Player", "Use this option to report rule-breaking or toxic behavior. Provide as much detail as possible.")
                .AddField("Won a Giveaway", "Use this option to claim a prize from a giveaway. Include the giveaway details.")
                .AddField("Get Support by Staff", "Use this option to get help from our support team. Describe your issue in detail.")
                .WithColor(Color.Blue)
                .WithCurrentTimestamp()
                .Build();

            await user.SendMessageAsync(embed: embed, components: component);
        }

        private static async Task InteractionCreatedAsync(SocketInteraction interaction)
        {
            try
            {
                if (interaction is SocketMessageComponent componentInteraction)
                {
                    switch (componentInteraction.Data.CustomId)
                    {
                        case "ticket_reason":
                            await HandleTicketReasonInteractionAsync(componentInteraction);
                            break;

                        case "claim_ticket":
                            await ClaimTicketAsync(componentInteraction);
                            break;

                        case "resolve_ticket":
                            await ResolveTicketAsync(componentInteraction);
                            break;

                        case "close_ticket":
                            await CloseTicketAsync(componentInteraction);
                            break;

                        case "admin_menu":
                            await HandleAdminMenuInteractionAsync(componentInteraction);
                            break;

                        default:
                            await componentInteraction.RespondAsync("Unknown interaction.", ephemeral: true);
                            break;
                    }
                }
                else if (interaction is SocketModal modalInteraction)
                {
                    switch (modalInteraction.Data.CustomId)
                    {
                        case "resolve_modal":
                            await HandleResolveModalAsync(modalInteraction);
                            break;

                        case "send_message_modal":
                            await HandleSendMessageModalAsync(modalInteraction);
                            break;

                        default:
                            await modalInteraction.RespondAsync("Unknown modal interaction.", ephemeral: true);
                            break;
                    }
                }
                else if (interaction is SocketSlashCommand slashCommand)
                {
                    if (slashCommand.CommandName == "admin")
                    {
                        await ShowAdminMenuAsync(slashCommand);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Interaction failed: {ex.Message}");
                await interaction.RespondAsync("An error occurred while processing your interaction. Please try again.", ephemeral: true);
            }
        }

        private static async Task HandleTicketReasonInteractionAsync(SocketMessageComponent componentInteraction)
        {
            // Get the selected reason
            var selectedReason = componentInteraction.Data.Values.First();
            string reasonType = selectedReason switch
            {
                "report" => "Report a Player",
                "giveaway" => "Won a Giveaway",
                "support" => "Get Support by Staff",
                _ => "Unknown"
            };

            // Acknowledge the interaction
            await componentInteraction.DeferAsync();

            // Create the ticket
            await CreateTicketAsync(componentInteraction.User, reasonType);

            // Update the last ticket creation time
            _lastTicketCreation[componentInteraction.User.Id] = DateTime.Now;
        }

        private static async Task CreateTicketAsync(SocketUser user, string reasonType)
        {
            _totalTickets++;
            string ticketChannelName = $"ticket-{user.Username}-{reasonType.Replace(" ", "-").ToLower()}";

            foreach (var guild in _client.Guilds)
            {
                var helperRole = guild.Roles.FirstOrDefault(r => r.Name == HELPER_ROLE_NAME);
                var modRole = guild.Roles.FirstOrDefault(r => r.Name == MOD_ROLE_NAME);
                var minecraftManagerRole = guild.Roles.FirstOrDefault(r => r.Name == "Minecraft Manager");
                var discordManagerRole = guild.Roles.FirstOrDefault(r => r.Name == "Discord Manager");
                var owner = guild.Owner;

                if (helperRole == null)
                {
                    Console.WriteLine($"Helper role '{HELPER_ROLE_NAME}' not found in guild '{guild.Name}'.");
                    continue;
                }

                // Find or create the "Tickets" category
                var ticketsCategory = guild.CategoryChannels.FirstOrDefault(c => c.Name == "Tickets");
                if (ticketsCategory == null)
                {
                    // Create the category and fetch it as a SocketCategoryChannel
                    var restCategory = await guild.CreateCategoryChannelAsync("Tickets");
                    ticketsCategory = guild.GetCategoryChannel(restCategory.Id); // Fetch the category as a SocketCategoryChannel
                    Console.WriteLine($"Created 'Tickets' category in guild '{guild.Name}'.");
                }

                // Define permissions
                var everyonePerms = new OverwritePermissions(viewChannel: PermValue.Deny);
                var helperPerms = new OverwritePermissions(viewChannel: PermValue.Allow, sendMessages: PermValue.Allow, attachFiles: PermValue.Allow);
                var userPerms = new OverwritePermissions(viewChannel: PermValue.Allow, sendMessages: PermValue.Allow, attachFiles: PermValue.Allow); // Allow user to send files
                var modPerms = new OverwritePermissions(viewChannel: PermValue.Allow, sendMessages: PermValue.Allow, attachFiles: PermValue.Allow);
                var ownerPerms = new OverwritePermissions(viewChannel: PermValue.Allow, sendMessages: PermValue.Allow, attachFiles: PermValue.Allow);
                var managerPerms = new OverwritePermissions(viewChannel: PermValue.Allow, sendMessages: PermValue.Allow, attachFiles: PermValue.Allow);

                var permissionOverwrites = new List<Overwrite>
        {
            new Overwrite(guild.EveryoneRole.Id, PermissionTarget.Role, everyonePerms),
            new Overwrite(helperRole.Id, PermissionTarget.Role, helperPerms),
            new Overwrite(user.Id, PermissionTarget.User, userPerms) // Allow the ticket creator to send files
        };

                if (minecraftManagerRole != null)
                {
                    permissionOverwrites.Add(new Overwrite(minecraftManagerRole.Id, PermissionTarget.Role, managerPerms));
                }

                if (discordManagerRole != null)
                {
                    permissionOverwrites.Add(new Overwrite(discordManagerRole.Id, PermissionTarget.Role, managerPerms));
                }

                if (modRole != null)
                {
                    permissionOverwrites.Add(new Overwrite(modRole.Id, PermissionTarget.Role, modPerms));
                }

                if (owner != null)
                {
                    permissionOverwrites.Add(new Overwrite(owner.Id, PermissionTarget.User, ownerPerms));
                }

                // Create the ticket channel within the "Tickets" category
                var ticketChannel = await guild.CreateTextChannelAsync(ticketChannelName, properties =>
                {
                    properties.PermissionOverwrites = permissionOverwrites;
                    properties.CategoryId = ticketsCategory.Id;
                });

                // Create the embedded message with ticket details
                var embed = new EmbedBuilder()
                    .WithTitle($"Ticket #{_totalTickets}")
                    .WithDescription($"**Ticket Created By:** {user.Mention}\n**Reason:** {reasonType}\n**Created At:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
                    .WithColor(Color.Blue)
                    .WithCurrentTimestamp()
                    .Build();

                var claimButton = new ButtonBuilder()
                    .WithCustomId("claim_ticket")
                    .WithLabel("Claim Ticket")
                    .WithStyle(ButtonStyle.Primary);

                var resolveButton = new ButtonBuilder()
                    .WithCustomId("resolve_ticket")
                    .WithLabel("Resolve Ticket")
                    .WithStyle(ButtonStyle.Success);

                var closeButton = new ButtonBuilder()
                    .WithCustomId("close_ticket")
                    .WithLabel("Close Ticket")
                    .WithStyle(ButtonStyle.Danger);

                var component = new ComponentBuilder()
                    .WithButton(claimButton)
                    .WithButton(resolveButton)
                    .WithButton(closeButton)
                    .Build();

                await ticketChannel.SendMessageAsync($"{helperRole.Mention} - New ticket from {user.Mention}", embed: embed, components: component);

                // Save ticket information to a file
                SaveTicketInfo(user, reasonType);

                Console.WriteLine($"Ticket #{_totalTickets} created by {user.Username} with reason: {reasonType}");

                // Notify the owner panel that a ticket was created
                NotifyOwnerPanel("A ticket was created!");
            }
        }
        private static void NotifyOwnerPanel(string message)
        {
            // Write the notification message under the "Select an option:" prompt
            Console.WriteLine($"\n{message}");
            Console.Write("Select an option: ");
        }

        private static async Task ClaimTicketAsync(SocketMessageComponent interaction)
        {
            var guildUser = interaction.User as SocketGuildUser;
            if (guildUser == null || !guildUser.Roles.Any(r => r.Name == HELPER_ROLE_NAME || r.Name == MOD_ROLE_NAME))
            {
                await interaction.RespondAsync("You do not have permission to claim this ticket.", ephemeral: true);
                return;
            }

            var ticketChannel = interaction.Channel as SocketTextChannel;
            if (ticketChannel == null)
            {
                await interaction.RespondAsync("This command can only be used in a ticket channel.", ephemeral: true);
                return;
            }

            // Grant the claiming helper exclusive permissions
            var helperPerms = new OverwritePermissions(viewChannel: PermValue.Allow, sendMessages: PermValue.Allow);
            await ticketChannel.AddPermissionOverwriteAsync(guildUser, helperPerms);

            // Remove send permissions for other helpers
            var helperRole = guildUser.Guild.Roles.FirstOrDefault(r => r.Name == HELPER_ROLE_NAME);
            if (helperRole != null)
            {
                var otherHelperPerms = new OverwritePermissions(viewChannel: PermValue.Allow, sendMessages: PermValue.Deny);
                await ticketChannel.AddPermissionOverwriteAsync(helperRole, otherHelperPerms);
            }

            await interaction.RespondAsync($"{guildUser.Mention} has claimed this ticket.");
        }

        private static async Task ResolveTicketAsync(SocketMessageComponent interaction)
        {
            var guildUser = interaction.User as SocketGuildUser;
            if (guildUser == null || !guildUser.Roles.Any(r => r.Name == HELPER_ROLE_NAME || r.Name == MOD_ROLE_NAME))
            {
                await interaction.RespondAsync("You do not have permission to resolve this ticket.", ephemeral: true);
                return;
            }

            // Create a modal for the resolution message
            var modal = new ModalBuilder()
                .WithCustomId("resolve_modal")
                .WithTitle("Resolve Ticket")
                .AddTextInput("Resolution Message", "resolution_message", TextInputStyle.Paragraph, "Enter the resolution message...")
                .Build();

            await interaction.RespondWithModalAsync(modal);
        }

        private static async Task HandleResolveModalAsync(SocketModal modalInteraction)
        {
            var resolutionMessage = modalInteraction.Data.Components.First(x => x.CustomId == "resolution_message").Value;

            var ticketChannel = modalInteraction.Channel as SocketTextChannel;
            if (ticketChannel == null)
            {
                await modalInteraction.RespondAsync("This command can only be used in a ticket channel.", ephemeral: true);
                return;
            }

            // Send the resolution message to the user
            var userMention = ticketChannel.Name.Split('-')[1]; // Extract username from channel name
            var user = _client.GetUser(userMention);
            if (user != null)
            {
                await user.SendMessageAsync($"Your ticket has been resolved. Resolution: {resolutionMessage}");
            }

            // Close the ticket
            await ticketChannel.SendMessageAsync("This ticket has been resolved and will be closed in 10 seconds.");
            await Task.Delay(10000);

            try
            {
                await ticketChannel.DeleteAsync();
                Console.WriteLine($"Ticket #{_totalTickets} has been resolved and closed.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error deleting ticket channel: {ex.Message}");
                await modalInteraction.RespondAsync("An error occurred while resolving the ticket.", ephemeral: true);
            }
        }

        private static async Task CloseTicketAsync(SocketMessageComponent interaction)
        {
            var guildUser = interaction.User as SocketGuildUser;
            if (guildUser == null || !guildUser.Roles.Any(r => r.Name == HELPER_ROLE_NAME || r.Name == MOD_ROLE_NAME))
            {
                await interaction.RespondAsync("You do not have permission to close this ticket.", ephemeral: true);
                return;
            }

            var ticketChannel = interaction.Channel as SocketTextChannel;
            if (ticketChannel == null)
            {
                await interaction.RespondAsync("This command can only be used in a ticket channel.", ephemeral: true);
                return;
            }

            // Defer the interaction to prevent errors
            await interaction.DeferAsync();

            // Notify the user that the ticket is being closed
            await ticketChannel.SendMessageAsync("This ticket is being closed by staff. The channel will be deleted in 10 seconds.");

            // Delay before deleting the channel
            await Task.Delay(10000);

            try
            {
                await ticketChannel.DeleteAsync();
                Console.WriteLine($"Ticket #{_totalTickets} has been closed.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error deleting ticket channel: {ex.Message}");
            }
        }

        private static async Task ShowAdminMenuAsync(SocketSlashCommand command)
        {
            var adminMenu = new SelectMenuBuilder()
                .WithCustomId("admin_menu")
                .WithPlaceholder("Select an admin action")
                .AddOption("Show Logs", "show_logs", "Display the ticket logs.")
                .AddOption("Show Bot Info", "show_bot_info", "Display basic bot information.")
                .AddOption("Send Message to User", "send_message", "Send a message to a specific user.")
                .AddOption("Show Ticket Messages", "show_ticket_messages", "Display messages sent in ticket channels.");

            var component = new ComponentBuilder()
                .WithSelectMenu(adminMenu)
                .Build();

            await command.RespondAsync("Please select an admin action:", components: component, ephemeral: true);
        }

        private static async Task HandleAdminMenuInteractionAsync(SocketMessageComponent interaction)
        {
            var selectedOption = interaction.Data.Values.First();
            switch (selectedOption)
            {
                case "show_logs":
                    await ShowLogsAsync(interaction);
                    break;

                case "show_bot_info":
                    await ShowBotInfoAsync(interaction);
                    break;

                case "send_message":
                    await SendMessageToUserAsync(interaction);
                    break;

                case "show_ticket_messages":
                    await ShowTicketMessagesAsync(interaction);
                    break;

                default:
                    await interaction.RespondAsync("Unknown option selected.", ephemeral: true);
                    break;
            }
        }

        private static async Task ShowLogsAsync(SocketMessageComponent interaction)
        {
            try
            {
                if (!File.Exists(TICKETS_LOG_FILE))
                {
                    await interaction.RespondAsync("No logs found. The log file does not exist.", ephemeral: true);
                    return;
                }

                var logs = await File.ReadAllTextAsync(TICKETS_LOG_FILE);
                if (string.IsNullOrWhiteSpace(logs))
                {
                    await interaction.RespondAsync("No logs found. The log file is empty.", ephemeral: true);
                    return;
                }

                // Check if the logs exceed 2000 characters
                if (logs.Length <= 2000)
                {
                    // Send the logs as a message
                    await interaction.RespondAsync($"**Ticket Logs:**\n```{logs}```", ephemeral: true);
                }
                else
                {
                    // Send the logs as a file attachment
                    using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(logs)))
                    {
                        await interaction.RespondWithFileAsync(stream, "ticket_logs.txt", "Here are the ticket logs:", ephemeral: true);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error showing logs: {ex.Message}");
                await interaction.RespondAsync("An error occurred while processing your request. Please try again.", ephemeral: true);
            }
        }

        private static async Task ShowTicketMessagesAsync(SocketMessageComponent interaction)
        {
            try
            {
                if (!File.Exists(TICKETS_LOG_FILE))
                {
                    await interaction.RespondAsync("No ticket messages found. The log file does not exist.", ephemeral: true);
                    return;
                }

                var logs = await File.ReadAllTextAsync(TICKETS_LOG_FILE);
                if (string.IsNullOrWhiteSpace(logs))
                {
                    await interaction.RespondAsync("No ticket messages found. The log file is empty.", ephemeral: true);
                    return;
                }

                // Check if the logs exceed 2000 characters
                if (logs.Length <= 2000)
                {
                    // Send the logs as a message
                    await interaction.RespondAsync($"**Ticket Messages:**\n```{logs}```", ephemeral: true);
                }
                else
                {
                    // Send the logs as a file attachment
                    using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(logs)))
                    {
                        await interaction.RespondWithFileAsync(stream, "ticket_messages.txt", "Here are the ticket messages:", ephemeral: true);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error showing ticket messages: {ex.Message}");
                await interaction.RespondAsync("An error occurred while processing your request. Please try again.", ephemeral: true);
            }
        }

        private static async Task ShowBotInfoAsync(SocketMessageComponent interaction)
        {
            var botInfo = $"**Bot Info:**\n- Total Tickets Created: {_totalTickets}\n- Last Ticket Created At: {_lastTicketCreation.Values.LastOrDefault()}";
            await interaction.RespondAsync(botInfo, ephemeral: true);
        }

        private static async Task SendMessageToUserAsync(SocketMessageComponent interaction)
        {
            // Create a modal for sending a message to a user
            var modal = new ModalBuilder()
                .WithCustomId("send_message_modal")
                .WithTitle("Send Message to User")
                .AddTextInput("User ID", "user_id", TextInputStyle.Short, "Enter the user's Discord ID...")
                .AddTextInput("Message", "message_content", TextInputStyle.Paragraph, "Enter the message to send...")
                .Build();

            await interaction.RespondWithModalAsync(modal);
        }

        private static async Task HandleSendMessageModalAsync(SocketModal modalInteraction)
        {
            var userIdInput = modalInteraction.Data.Components.First(x => x.CustomId == "user_id").Value;
            var messageContent = modalInteraction.Data.Components.First(x => x.CustomId == "message_content").Value;

            if (!ulong.TryParse(userIdInput, out var userId))
            {
                await modalInteraction.RespondAsync("Invalid user ID. Please provide a valid Discord ID.", ephemeral: true);
                return;
            }

            var user = _client.GetUser(userId);
            if (user == null)
            {
                await modalInteraction.RespondAsync("User not found. Please ensure the user ID is correct.", ephemeral: true);
                return;
            }

            try
            {
                await user.SendMessageAsync(messageContent);
                await modalInteraction.RespondAsync($"Message sent to {user.Username} (ID: {user.Id}).", ephemeral: true);

                // Log the message in the log channel
                var logChannel = _client.GetChannel(LOG_CHANNEL_ID) as IMessageChannel;
                if (logChannel != null)
                {
                    await logChannel.SendMessageAsync($"**Message sent to {user.Username} (ID: {user.Id}):**\n{messageContent}");
                }
            }
            catch (Exception ex)
            {
                await modalInteraction.RespondAsync($"Failed to send message: {ex.Message}", ephemeral: true);
            }
        }

        private static void SaveTicketInfo(SocketUser user, string reasonType)
        {
            try
            {
                string ticketInfo = $"[{DateTime.Now}] Ticket #{_totalTickets} created by {user.Username} ({user.Id}) with reason: {reasonType}\n";
                File.AppendAllText(TICKETS_LOG_FILE, ticketInfo);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving ticket info: {ex.Message}");
            }
        }

        private static void LogTicketMessage(SocketMessage message)
        {
            try
            {
                string logMessage = $"[{DateTime.Now}] Message in ticket channel {message.Channel.Name} by {message.Author.Username}: {message.Content}\n";
                File.AppendAllText(TICKETS_LOG_FILE, logMessage);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error logging ticket message: {ex.Message}");
            }
        }

        private static async Task ReadyAsync()
        {
            // Clear the console
            Console.Clear();

            // Display bot connection message
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Bot is ready and connected!");
            Console.ResetColor();

            // Set bot activity
            await _client.SetActivityAsync(new Game("Managing tickets"));

            // Send the admin panel to the log channel
            await SendAdminPanelAsync();

            // Run the owner panel on a separate thread to avoid blocking the gateway
            _ = Task.Run(() => ShowOwnerPanelAsync());
        }

        private static async Task OnDisconnectedAsync(Exception exception)
        {
            // Change console text color to red
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Bot has disconnected.");
            Console.ResetColor();

            // Log the disconnection reason
            Console.WriteLine($"Disconnection reason: {exception?.Message}");
        }

        private static async Task ShowOwnerPanelAsync()
        {
            while (true)
            {
                // Clear the console and display the title
                Console.Clear();
                Console.Title = "Echo Ticket Tool";
                Console.WriteLine("        ");
                Console.WriteLine("        ");
                Console.WriteLine("        ");
                Console.WriteLine("        ");
                Console.WriteLine("                     ███████╗░█████╗░██╗░░██╗░█████╗░  ████████╗██╗░█████╗░██╗░░██╗███████╗████████╗");
                Console.WriteLine("                     ██╔════╝██╔══██╗██║░░██║██╔══██╗  ╚══██╔══╝██║██╔══██╗██║░██╔╝██╔════╝╚══██╔══╝");
                Console.WriteLine("                     █████╗░░██║░░╚═╝███████║██║░░██║  ░░░██║░░░██║██║░░╚═╝█████═╝░█████╗░░░░░██║░░░");
                Console.WriteLine("                     ██╔══╝░░██║░░██╗██╔══██║██║░░██║  ░░░██║░░░██║██║░░██╗██╔═██╗░██╔══╝░░░░░██║░░░");
                Console.WriteLine("                     ███████╗╚█████╔╝██║░░██║╚█████╔╝  ░░░██║░░░██║╚█████╔╝██║░╚██╗███████╗░░░██║░░░");
                Console.WriteLine("                     ███████╗╚█████╔╝██║░░██║╚█████╔╝  ░░░██║░░░██║╚█████╔╝██║░╚██╗███████╗░░░██║░░░");
                Console.WriteLine("                     ╚══════╝░╚════╝░╚═╝░░╚═╝░╚════╝░  ░░░╚═╝░░░╚═╝░╚════╝░╚═╝░░╚═╝╚══════╝░░░╚═╝░░░");
                Console.WriteLine("        ");
                Console.WriteLine("                                     ████████╗░█████╗░░█████╗░██╗░░░░░");
                Console.WriteLine("                                     ╚══██╔══╝██╔══██╗██╔══██╗██║░░░░░");
                Console.WriteLine("                                     ░░░██║░░░██║░░██║██║░░██║██║░░░░░");
                Console.WriteLine("                                     ░░░██║░░░██║░░██║██║░░██║██║░░░░░");
                Console.WriteLine("                                     ░░░██║░░░╚█████╔╝╚█████╔╝███████╗");
                Console.WriteLine("                                     ░░░╚═╝░░░░╚════╝░░╚════╝░╚══════╝");

                // Display "Bot is ready and connected!" below the title
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("                                     Bot is ready and connected!");
                Console.ResetColor();

                // Display "Owner Panel" in red
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("                                     === Owner Panel ===");
                Console.ResetColor();

                Console.WriteLine("                                     1. Send Private DM Message to User");
                Console.WriteLine("                                     2. Turn Off the Bot");
                Console.WriteLine("                                     3. Send Ticket Dropdown to Channel");
                Console.WriteLine("                                     4. Exit");
                Console.WriteLine("                                     5. Turn on bot ");
                Console.WriteLine("                                     6. Resend Admin Panel");
                Console.Write("                                     Select an option: ");
                var input = Console.ReadLine();

                switch (input)
                {
                    case "1":
                        await SendPrivateDMMessageToChannelAsync();
                        break;

                    case "2":
                        await TurnOffBotAsync();
                        break;

                    case "3":
                        await SendTicketDropdownAsync();
                        Console.WriteLine("Ticket dropdown sent to the channel.");
                        break;

                    case "4":
                        return;

                    case "5":
                        await TurnOnBotAsync();
                        break;

                    case "6":
                        await SendAdminPanelAsync(); // Call the method to resend the admin panel
                        Console.WriteLine("Admin panel resent.");
                        break;

                    default:
                        Console.WriteLine("Invalid option. Please try again.");
                        break;
                }

                // Wait for user input before clearing the console and re-displaying the menu
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey();
            }
        }
        private static async Task SendPrivateDMMessageToChannelAsync()
        {
            Console.Write("Enter the user ID: ");
            var userIdInput = Console.ReadLine();
            if (!ulong.TryParse(userIdInput, out var userId))
            {
                Console.WriteLine("Invalid user ID.");
                return;
            }

            var user = _client.GetUser(userId);
            if (user == null)
            {
                Console.WriteLine("User not found.");
                return;
            }

            Console.Write("Enter the message to send: ");
            var message = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(message))
            {
                Console.WriteLine("Message cannot be empty.");
                return;
            }

            // Send the message to the user
            await user.SendMessageAsync(message);

            // Send the message to the log channel
            var logChannel = _client.GetChannel(LOG_CHANNEL_ID) as IMessageChannel;
            if (logChannel != null)
            {
                await logChannel.SendMessageAsync($"**Message sent to {user.Username} (ID: {user.Id}):**\n{message}");
            }

            Console.WriteLine("Message sent successfully.");
        }

        private static async Task TurnOffBotAsync()
        {
            Console.WriteLine("Turning off the bot...");
            await _client.StopAsync();
            Console.WriteLine("Bot has been turned off.");
        }

        private static async Task TurnOnBotAsync()
        {
            Console.WriteLine("Turning on the bot...");
            await _client.StartAsync();
            Console.WriteLine("Bot has been turned on.");
        }

        private static async Task SendTicketDropdownAsync()
        {
            var channel = _client.GetChannel(TICKET_CHANNEL_ID) as IMessageChannel;
            if (channel == null)
            {
                Console.WriteLine("Specified channel not found.");
                return;
            }

            // Create the dropdown menu for ticket creation
            var selectMenu = new SelectMenuBuilder()
                .WithCustomId("ticket_reason")
                .WithPlaceholder("Select a reason for your ticket")
                .AddOption("Report a Player", "report", "Report rule-breaking or toxic behavior.")
                .AddOption("Won a Giveaway", "giveaway", "Claim a prize from a giveaway.")
                .AddOption("Get Support by Staff", "support", "Get help from our support team.");

            var component = new ComponentBuilder()
                .WithSelectMenu(selectMenu)
                .Build();

            // Create the embedded message with ticket details
            var embed = new EmbedBuilder()
                .WithTitle("Echo Tickets")
                .WithDescription("Please select a reason for creating a ticket from the dropdown menu below.")
                .AddField("Report a Player", "Use this option to report rule-breaking or toxic behavior. Provide as much detail as possible.")
                .AddField("Won a Giveaway", "Use this option to claim a prize from a giveaway. Include the giveaway details.")
                .AddField("Get Support by Staff", "Use this option to get help from our support team. Describe your issue in detail.")
                .WithColor(Color.Blue)
                .WithCurrentTimestamp()
                .Build();

            await channel.SendMessageAsync(embed: embed, components: component);
        }

        private static async Task SendAdminPanelAsync()
        {
            var adminMenu = new SelectMenuBuilder()
                .WithCustomId("admin_menu")
                .WithPlaceholder("Select an admin action")
                .AddOption("Show Logs", "show_logs", "Display the ticket logs.")
                .AddOption("Show Bot Info", "show_bot_info", "Display basic bot information.")
                .AddOption("Send Message to User", "send_message", "Send a message to a specific user.")
                .AddOption("Show Ticket Messages", "show_ticket_messages", "Display messages sent in ticket channels.");

            var component = new ComponentBuilder()
                .WithSelectMenu(adminMenu)
                .Build();

            var logChannel = _client.GetChannel(LOG_CHANNEL_ID) as IMessageChannel;
            if (logChannel != null)
            {
                await logChannel.SendMessageAsync("**Admin Panel**", components: component);
            }
        }
    }
}