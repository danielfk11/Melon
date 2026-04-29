# Changelog

## 1.0.0-preview.3 - 2026-04-28

- Modo opcional `exactlyOnce` para filas classicas, com deduplicacao broker-side por `messageId`.
- Tombstones de dedupe preservados em filas duraveis para manter o comportamento apos restart.
- Teste de integracao cobrindo supressao de duplicate publish apos restart em fila `exactlyOnce`.
- Persistencia de topologia de exchanges/bindings duraveis com reload no startup do broker.
- Replicacao de declare/bind/unbind de exchanges no modelo de cluster atual.
- Publisher confirm duravel para publish em filas duraveis e streams duraveis, aguardando flush/fsync antes do sucesso.
- Replicacao de publish em cluster alinhada ao mesmo contrato de confirmacao duravel.
- Persistencia de offsets de stream consumer/group para filas duraveis, com retomada apos restart do broker.
- Teste de integracao cobrindo `StreamAck` com restart no mesmo `DataDirectory`.
- Teste unitario cobrindo publisher confirm duravel com `BatchFlushMs` atrasado.

## 1.0.0-preview.2 - 2026-04-28

- Alinhamento do repositorio com o estado publicado apos o preview inicial.
- Metadados de NuGet do `MelonMQ.Protocol` e `MelonMQ.Broker` completados com readme, licenca e links de repositorio.
- Pacotes prontos para o proximo ciclo de publicacao sem os warnings de metadata vistos no preview anterior.

## 1.0.0-preview.1 - 2026-04-28

- Posicionamento publico inicial como preview tecnica.
- Cliente .NET com suporte a AUTH via `AuthenticateAsync`, credenciais em `MelonConnectionOptions` e auto-auth por URI.
- Validacao de integracao para `RequireAuth=true` com TLS habilitado.
- Metadados de publicacao do pacote `MelonMQ.Client` completos para NuGet.
- Broker empacotavel como tool/pacote preview.
- Hardening pre-release no broker com atualizacao do OpenTelemetry e migracao para `X509CertificateLoader` no carregamento do certificado TLS.