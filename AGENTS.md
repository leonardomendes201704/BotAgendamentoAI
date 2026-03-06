# Diretrizes do Agent

## Premissa obrigatoria para parametros

- Todo parametro funcional/configuravel deve ser persistido em banco de dados.
- Todo parametro funcional/configuravel deve ser editavel na UI do Admin, na tela de Configuracoes.
- Nao deixar parametros de negocio hardcoded sem opcao de configuracao via banco + UI.
- Sempre definir valor padrao seguro para novos parametros e tratar retrocompatibilidade para tenants antigos.

