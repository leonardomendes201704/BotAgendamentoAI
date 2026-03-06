using BotAgendamentoAI.Telegram.Application.Callback;
using BotAgendamentoAI.Telegram.Application.Common;
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
            new KeyboardButton[] { MenuTexts.ClientRequestService, MenuTexts.ClientMyBookings },
            new KeyboardButton[] { MenuTexts.ClientFavorites, MenuTexts.ClientHelp },
            new KeyboardButton[] { MenuTexts.ClientSwitchToProvider }
        })
        {
            ResizeKeyboard = true,
            IsPersistent = true
        };
    }

    public static InlineKeyboardMarkup ClientHomeActions(bool allowSwitchToProvider)
    {
        var rows = new List<InlineKeyboardButton[]>
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("1 - Pedir servico", "C:HOME:REQ")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("2 - Meus agendamentos", "C:HOME:MY")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("3 - Favoritos", "C:HOME:FAV")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("4 - Ajuda", "C:HOME:HLP")
            }
        };

        if (allowSwitchToProvider)
        {
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("5 - Trocar para Prestador", "C:HOME:SWP")
            });
        }

        return new InlineKeyboardMarkup(rows);
    }

    public static ReplyKeyboardMarkup ProviderMenu()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new KeyboardButton[] { MenuTexts.ProviderAvailableJobs, MenuTexts.ProviderAgenda },
            new KeyboardButton[] { MenuTexts.ProviderProfile, MenuTexts.ProviderPortfolio },
            new KeyboardButton[] { MenuTexts.ProviderSettings, MenuTexts.ProviderSwitchToClient }
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
                KeyboardButton.WithRequestLocation("Enviar localizacao")
            },
            new KeyboardButton[]
            {
                new KeyboardButton(MenuTexts.Back),
                new KeyboardButton(MenuTexts.Cancel)
            }
        })
        {
            ResizeKeyboard = true,
            IsPersistent = true,
            OneTimeKeyboard = false
        };
    }

    public static ReplyKeyboardMarkup CepRequestKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new KeyboardButton[]
            {
                new KeyboardButton(MenuTexts.Back),
                new KeyboardButton(MenuTexts.Cancel)
            }
        })
        {
            ResizeKeyboard = true,
            IsPersistent = true,
            OneTimeKeyboard = false
        };
    }

    public static InlineKeyboardMarkup AddressConfirmation()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Correto", "C:ADDR:OK"),
                InlineKeyboardButton.WithCallbackData("Alterar", "C:ADDR:EDIT")
            },
            NavigationRow()
        });
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
        var days = new List<DateTime>();
        for (var i = 0; i < 7; i++)
        {
            days.Add(nowLocal.Date.AddDays(i));
        }

        return DaySelection(days);
    }

    public static InlineKeyboardMarkup DaySelection(IReadOnlyList<DateTime> days)
    {
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var day in days)
        {
            var date = day.Date;
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
        var slots = new[] { "0800", "1000", "1300", "1500", "1800" };
        return TimeSelection(yyyymmdd, slots);
    }

    public static InlineKeyboardMarkup TimeSelection(string yyyymmdd, IReadOnlyList<string> hhmmSlots)
    {
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var slot in hhmmSlots)
        {
            var digits = new string((slot ?? string.Empty).Where(char.IsDigit).ToArray());
            if (digits.Length != 4)
            {
                continue;
            }

            var label = $"{digits[..2]}:{digits[2..]}";
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData(label, $"C:TIM:{yyyymmdd}:{digits}")
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
                InlineKeyboardButton.WithCallbackData("Chat", $"J:{jobId}:CHAT")
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

    public static InlineKeyboardMarkup ClientRescheduleDaySelection(long jobId, DateTimeOffset nowLocal)
    {
        var days = new List<DateTime>();
        for (var i = 0; i < 7; i++)
        {
            days.Add(nowLocal.Date.AddDays(i));
        }

        return ClientRescheduleDaySelection(jobId, days);
    }

    public static InlineKeyboardMarkup ClientRescheduleDaySelection(long jobId, IReadOnlyList<DateTime> days)
    {
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var day in days)
        {
            var date = day.Date;
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData(date.ToString("dd/MM (ddd)"), $"J:{jobId}:RS:DAY:{date:yyyyMMdd}")
            });
        }

        rows.Add(NavigationRow());
        return new InlineKeyboardMarkup(rows);
    }

    public static InlineKeyboardMarkup ClientRescheduleTimeSelection(long jobId, string yyyymmdd)
    {
        var slots = new[] { "0800", "1000", "1300", "1500", "1800" };
        return ClientRescheduleTimeSelection(jobId, yyyymmdd, slots);
    }

    public static InlineKeyboardMarkup ClientRescheduleTimeSelection(long jobId, string yyyymmdd, IReadOnlyList<string> hhmmSlots)
    {
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var slot in hhmmSlots)
        {
            var digits = new string((slot ?? string.Empty).Where(char.IsDigit).ToArray());
            if (digits.Length != 4)
            {
                continue;
            }

            var label = $"{digits[..2]}:{digits[2..]}";
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData(label, $"J:{jobId}:RS:TIM:{yyyymmdd}{digits}")
            });
        }

        rows.Add(NavigationRow());
        return new InlineKeyboardMarkup(rows);
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

    public static InlineKeyboardMarkup ProviderProfileActions()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Editar bio", "P:PRF:BIO"),
                InlineKeyboardButton.WithCallbackData("Editar categorias", "P:PRF:CAT")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Editar raio", "P:PRF:RAD"),
                InlineKeyboardButton.WithCallbackData("Definir local base", "P:PRF:LOC")
            },
            NavigationRow()
        });
    }

    public static InlineKeyboardMarkup ProviderCategorySelection(
        IReadOnlyList<ServiceCategoryEntity> categories,
        ISet<string> selectedNames)
    {
        var rows = new List<InlineKeyboardButton[]>();

        foreach (var category in categories.Take(20))
        {
            var selected = selectedNames.Contains(category.Name);
            var label = selected ? $"[x] {category.Name}" : $"[ ] {category.Name}";
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData(label, $"P:CAT:{category.Id}")
            });
        }

        rows.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("Salvar categorias", "P:CATSAVE")
        });

        rows.Add(NavigationRow());
        return new InlineKeyboardMarkup(rows);
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
            InlineKeyboardButton.WithCallbackData(MenuTexts.Back, CallbackDataRouter.Back()),
            InlineKeyboardButton.WithCallbackData(MenuTexts.Cancel, CallbackDataRouter.Cancel())
        };
    }
}
