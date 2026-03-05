namespace BotAgendamentoAI.Telegram.Application.Common;

public static class BotMessages
{
    public const string StateExpired = "Vamos retomar do inicio. Escolha uma opcao no menu.";
    public const string CallbackExpired = "Essa acao expirou. Vou te levar para o menu principal.";
    public const string UnknownCommand = "Nao entendi. Use os botoes abaixo para continuar.";

    public static string WelcomeRoleChoice()
        => "Bem-vindo ao ConsertaPraMim! Voce e Cliente ou Prestador?";

    public static string ClientHomeMenu()
        => "Menu Cliente:\nEscolha uma opcao:";

    public static string ProviderHomeMenu()
        => "Menu Prestador:\nEscolha uma opcao:";

    public static string AskCategory()
        => "1/7 - Qual categoria do servico?";

    public static string AskDescription()
        => "2/7 - Descreva o problema em poucas linhas.";

    public static string AskPhotos()
        => "3/7 - Envie fotos do problema. Quando terminar, toque em 'Concluir fotos'.";

    public static string AskLocation()
        => "4/7 - Envie sua localizacao ou digite o endereco com CEP.";

    public static string AskSchedule()
        => "5/7 - Quando voce precisa do servico?";

    public static string AskPreference()
        => "6/7 - Qual sua preferencia para selecionar prestador?";

    public static string AskConfirm(string summary)
        => $"7/7 - Confirme seu pedido:\n\n{summary}";

    public static string WaitingProvider()
        => "Pedido criado! Estamos buscando um prestador disponivel.";

    public static string NoProviderJobs()
        => "Nao encontrei pedidos disponiveis agora. Tente novamente em instantes.";

    public static string PortfolioUploadHint()
        => "Envie fotos para seu portfolio. Toque em 'Concluir upload' quando terminar.";

    public static string ChatOpened()
        => "Chat mediado ativo. Envie mensagens normalmente. Use /sairchat para encerrar.";

    public static string ChatClosed()
        => "Chat encerrado.";

    public static string NeedAddressWithCep()
        => "Endereco invalido. Envie um endereco com CEP (8 digitos).";

    public static string InvalidFinishFormat()
        => "Formato invalido. Envie: valor | observacoes\nEx: 180.00 | troca de capacitor e limpeza.";
}
