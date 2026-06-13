# Changelog

## 1.3.1 - 2026-06-12

- Painel operacional agora mostra consumers ativos por fila e uso de prefetch/in-flight em percentual.

## 1.3.0 - 2026-06-06

- Backpressure configuravel para publish em fila classica, com erro TCP/HTTP previsivel e metrica Prometheus de rejeicao.
- Scripts oficiais para benchmark, validacao DR, validacao operacional, hash PBKDF2 e pacote operacional sem Docker.
- Regras Prometheus padrao para broker down, backlog alto, in-flight alto, backpressure e erros.
- Release workflow agora tambem gera pacote operacional sem Docker.

## 1.2.0 - 2026-06-04

- Stack local de observabilidade sem Docker com Prometheus e Grafana.
- Dashboard Grafana provisionado para throughput, backlog, in-flight, latencia e erros.
- Metricas Prometheus de backlog por fila (`melonmq_queue_pending_messages`, `melonmq_queue_inflight_messages`, `melonmq_queues_total`).
- Auto-start opcional da stack local em ambiente Development.

## 1.1.0 - 2026-05-24

- Streams particionados com `partitionCount` e publish por `partitionKey`/`partition`.
- Rebalance automatico de consumer groups em stream com retomada por offset de particao.
- Metricas de lag por grupo/particao para stream (`melonmq_stream_group_partition_lag` e `melonmq_stream_partition_lag_max`).

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

## 1.0.0-preview.5 - 2026-04-28

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
