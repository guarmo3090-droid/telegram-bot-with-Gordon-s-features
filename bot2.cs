using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting; 
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace UniversityBot
{
    public class BotConfig
    {
        public string TelegramToken { get; set; } = null!;
        public string OpenAiKey { get; set; } = null!;
        public string SystemPrompt { get; set; } = null!;
    }

    public class MessageItem
    {
        public string role { get; set; } = null!;
        public string content { get; set; } = null!;
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureServices((context, services) =>
                {
                    services.Configure<BotConfig>(context.Configuration.GetSection("BotConfig"));
                    services.AddHostedService<TelegramBotWorker>();
                })
                .Build();

            Console.WriteLine("Запуск сервера бота...");
            await host.RunAsync();
        }
    }

    public class TelegramBotWorker : BackgroundService
    {
        private readonly BotConfig _config;
        private readonly ILogger<TelegramBotWorker> _logger;
        private readonly TelegramBotClient _botClient;
        private readonly HttpClient _httpClient;
        private readonly ConcurrentDictionary<long, List<MessageItem>> _userContext = new();

        public TelegramBotWorker(IOptions<BotConfig> options, ILogger<TelegramBotWorker> logger)
        {
            _config = options.Value;
            _logger = logger;
            _botClient = new TelegramBotClient(_config.TelegramToken);
            
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.OpenAiKey);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var receiverOptions = new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() };

            _botClient.StartReceiving(
                updateHandler: HandleUpdateAsync,
                errorHandler: HandleErrorAsync,
                receiverOptions: receiverOptions,
                cancellationToken: stoppingToken
            );

            _logger.LogInformation("Бот-перекладач успішно запущений і готовий до роботи!");
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }

        private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            if (update.Message is not { } message || message.From is null)
                return;

            long chatId = message.Chat.Id;
            long userId = message.From.Id;

            if (message.Type != MessageType.Text || string.IsNullOrWhiteSpace(message.Text))
            {
                await botClient.SendMessage(chatId, "Я працюю лише з текстом. Картинки, стікери чи файли я не розумію.", cancellationToken: cancellationToken);
                return;
            }

            string text = message.Text;

            if (text.StartsWith("/"))
            {
                if (text == "/start")
                {
                    _userContext[userId] = new List<MessageItem>();
                    await botClient.SendMessage(chatId, "Привіт! Я AI-перекладач. Напиши мені текст українською або англійською, і я його перекладу.", cancellationToken: cancellationToken);
                }
                else if (text == "/reset")
                {
                    _userContext.TryRemove(userId, out _);
                    await botClient.SendMessage(chatId, "Історію діалогу очищено. Почнемо з чистого аркуша!", cancellationToken: cancellationToken);
                }
                else
                {
                    await botClient.SendMessage(chatId, "Невідома команда. Доступні команди: /start, /reset.", cancellationToken: cancellationToken);
                }
                return;
            }

            var history = _userContext.GetOrAdd(userId, _ => new List<MessageItem>());
            history.Add(new MessageItem { role = "user", content = text });

            if (history.Count > 10)
            {
                history = history.Skip(history.Count - 10).ToList();
                _userContext[userId] = history;
            }

            try
            {
                await botClient.SendChatAction(chatId, ChatAction.Typing, cancellationToken: cancellationToken);

                var requestBody = new
                {
                    model = "gpt-4o-mini",
                    instructions = _config.SystemPrompt,
                    input = history,
                    temperature = 0.3
                };

                var response = await _httpClient.PostAsJsonAsync("https://api.openai.com/v1/responses", requestBody, cancellationToken);
                string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"OpenAI API Error: {response.StatusCode} - {responseJson}");
                    await botClient.SendMessage(chatId, $"Помилка OpenAI: {response.StatusCode}. Деталі в консолі.", cancellationToken: cancellationToken);
                    
                    history.RemoveAt(history.Count - 1); 
                    return;
                }

            using JsonDocument doc = JsonDocument.Parse(responseJson);
            string answer = "Не вдалося розпізнати відповідь моделі.";
            
            try 
            {
                if (doc.RootElement.TryGetProperty("output", out JsonElement outputArray) && outputArray.GetArrayLength() > 0)
                {
                    if (outputArray[0].TryGetProperty("content", out JsonElement contentArray) && contentArray.GetArrayLength() > 0)
                    {
                        if (contentArray[0].TryGetProperty("text", out JsonElement textElement))
                        {
                            answer = textElement.GetString() ?? answer;
                        }
                    }
                }
                else if (doc.RootElement.TryGetProperty("choices", out JsonElement choices) && choices.GetArrayLength() > 0)
                {
                    answer = choices[0].GetProperty("message").GetProperty("content").GetString() ?? answer;
                }
                else if (doc.RootElement.TryGetProperty("output_text", out JsonElement outText))
                {
                    answer = outText.GetString() ?? answer;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Помилка парсингу: {ex.Message}\nОригінальний JSON: {responseJson}");
            }

            history.Add(new MessageItem { role = "assistant", content = answer });
            await botClient.SendMessage(chatId, answer, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Помилка: {ex.Message}");
            await botClient.SendMessage(chatId, "Виникла технічна помилка при зверненні до моделі. Спробуй пізніше.", cancellationToken: cancellationToken);
        }
    }

        private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            _logger.LogError($"Помилка Telegram API: {exception.Message}");
            return Task.CompletedTask;
        }
    }
}