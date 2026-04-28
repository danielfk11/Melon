# Changelog

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