using BotAgendamentoAI.Telegram.Application.Callback;
using BotAgendamentoAI.Telegram.Domain.Entities;
using BotAgendamentoAI.Telegram.TelegramCompat.Types.ReplyMarkups;

namespace BotAgendamentoAI.Telegram.Application.Services;

public static class KeyboardFactory
{
    public static InlineKeyboardMarkup RoleChoice()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Cliente", CallbackDataRouter.RoleClient()),
                InlineKeyboardButton.WithCallbackData("Prestador", CallbackDataRouter.RoleProvider())
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Cliente + Prestador", CallbackDataRouter.RoleBoth())
            }
        });
    }

    public static ReplyKeyboardMarkup ClientMenu()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new KeyboardButton[] { "??? Pedir servico", "?? Meus agendamentos" },
            new KeyboardButton[] { "? Favoritos", "? Ajuda" },
            new KeyboardButton[] { "?? Trocar para Prestador" }
        })
        {
            ResizeKeyboard = true,
            IsPersistent = true
        };
    }

    public static ReplyKeyboardMarkup ProviderMenu()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new KeyboardButton[] { "?? Pedidos disponiveis", "?? Minha agenda" },
            new KeyboardButton[] { "?? Meu perfil", "?? Portfolio" },
            new KeyboardButton[] { "?? Configuracoes", "?? Trocar para Cliente" }
        })
        {
            ResizeKeyboard = true,
            IsPersistent = true
        };
    }

    public static InlineKeyboardMarkup Categories(IReadOnlyList<ServiceCategoryEntity> categories)
    {
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var item in categories.Take(16))
        {
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData(item.Name, $"C:CAT:{item.Id}")
            });
        }

        rows.Add(NavigationRow());
        return new InlineKeyboardMarkup(rows);
    }

    public static InlineKeyboardMarkup PhotoCollectMenu()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("Concluir fotos", "C:PH:DONE") },
            NavigationRow()
        });
    }

    public static ReplyKeyboardMarkup LocationRequestKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new KeyboardButton[]
            {
                KeyboardButton.WithRequestLocation("?? Enviar localizacao")
            },
            new KeyboardButton[]
            {
                new KeyboardButton("Voltar"),
                new KeyboardButton("Cancelar")
            }
        })
        {
            ResizeKeyboard = true,
            IsPersistent = true,
            OneTimeKeyboard = false
        };
    }

    public static InlineKeyboardMarkup ScheduleMode()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Urgente", "C:SCH:URG"),
                InlineKeyboardButton.WithCallbackData("Hoje", "C:SCH:TOD")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Agendar", "C:SCH:CAL")
            },
            NavigationRow()
        });
    }

    public static InlineKeyboardMarkup DaySelection(DateTimeOffset nowLocal)
    {
        var rows = new List<InlineKeyboardButton[]>();
        for (var i = 0; i < 7; i++)
        {
            var date = nowLocal.Date.AddDays(i);
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData(date.ToString("dd/MM (ddd)"), $"C:DAY:{date:yyyyMMdd}")
            });
        }

        rows.Add(NavigationRow());
        return new InlineKeyboardMarkup(rows);
    }

    public static InlineKeyboardMarkup TimeSelection(string yyyymmdd)
    {
        var slots = new[] { "08:00", "10:00", "13:00", "15:00", "18:00" };
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var slot in slots)
        {
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData(slot, $"C:TIM:{yyyymmdd}:{slot.Replace(":", string.Empty)}")
            });
        }

        rows.Add(NavigationRow());
        return new InlineKeyboardMarkup(rows);
    }

    public static InlineKeyboardMarkup Preferences()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Menor preco", "C:PRF:LOW"),
                InlineKeyboardButton.WithCallbackData("Melhor avaliados", "C:PRF:RAT")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Mais rapido", "C:PRF:FAST"),
                InlineKeyboardButton.WithCallbackData("Escolher prestador", "C:PRF:CHO")
            },
            NavigationRow()
        });
    }

    public static InlineKeyboardMarkup ConfirmRequest()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Confirmar", "C:CONF:OK"),
                InlineKeyboardButton.WithCallbackData("Editar", "C:CONF:EDIT")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Cancelar", "C:CONF:CANCEL")
            }
        });
    }

    public static InlineKeyboardMarkup JobCardActions(long jobId)
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Ver detalhes", $"J:{jobId}:DET"),
                InlineKeyboardButton.WithCallbackData("Aceitar", $"J:{jobId}:ACC")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Recusar", $"J:{jobId}:REJ")
            }
        });
    }

    public static InlineKeyboardMarkup ProviderTimeline(long jobId)
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Estou a caminho", $"J:{jobId}:S:OTW"),
                InlineKeyboardButton.WithCallbackData("Cheguei", $"J:{jobId}:S:ARR")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Iniciar servico", $"J:{jobId}:S:STA"),
                InlineKeyboardButton.WithCallbackData("Finalizar", $"J:{jobId}:S:FIN")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("?? Chat", $"J:{jobId}:CHAT")
            }
        });
    }

    public static InlineKeyboardMarkup FinishWizardActions(long jobId)
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Concluir finalizacao", $"J:{jobId}:S:DONE")
            },
            NavigationRow()
        });
    }

    public static InlineKeyboardMarkup ChatActions(long jobId)
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Encerrar chat", $"J:{jobId}:CHAT:EXIT")
            }
        });
    }

    public static InlineKeyboardMarkup Rating(long jobId)
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("1", $"R:{jobId}:1"),
                InlineKeyboardButton.WithCallbackData("2", $"R:{jobId}:2"),
                InlineKeyboardButton.WithCallbackData("3", $"R:{jobId}:3"),
                InlineKeyboardButton.WithCallbackData("4", $"R:{jobId}:4"),
                InlineKeyboardButton.WithCallbackData("5", $"R:{jobId}:5")
            }
        });
    }

    public static InlineKeyboardMarkup PortfolioMenu()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Enviar fotos", "P:POR:UP"),
                InlineKeyboardButton.WithCallbackData("Ver portfolio", "P:POR:VW:0")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Remover foto", "P:POR:RM:0")
            },
            NavigationRow()
        });
    }

    public static InlineKeyboardMarkup GalleryNext(string prefix, int nextOffset)
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Ver mais fotos", $"{prefix}:{nextOffset}")
            },
            NavigationRow()
        });
    }

    public static InlineKeyboardButton[] NavigationRow()
    {
        return new[]
        {
            InlineKeyboardButton.WithCallbackData("Voltar", CallbackDataRouter.Back()),
            InlineKeyboardButton.WithCallbackData("Cancelar", CallbackDataRouter.Cancel())
        };
    }
}
