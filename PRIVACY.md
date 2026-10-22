# Política de privacidade / Privacy policy

*English version below.*

O GameStream não coleta dados de uso, não tem anúncios e não envia nada para os autores do programa. Ele não tem conta nem login. Os arquivos de registro (log) e as configurações ficam só no seu PC, em `%LOCALAPPDATA%\GameStream`.

O programa usa serviços de terceiros nestes casos:

| Quando | Serviço | O que é enviado |
|---|---|---|
| Na primeira vez que o app abre | GitHub (github.com) | Download do FFmpeg ([BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)) e do `cloudflared` ([cloudflare/cloudflared](https://github.com/cloudflare/cloudflared)). |
| Enquanto o app está aberto | Cloudflare Quick Tunnel (trycloudflare.com) | O app abre um túnel para criar o link público da sua transmissão ou sala. O vídeo, o som e as mensagens da sala passam pela Cloudflare até quem estiver assistindo. Veja a [política de privacidade da Cloudflare](https://www.cloudflare.com/privacypolicy/). |
| Se você tem salas salvas | ntfy.sh | Para o código das salas salvas continuar valendo quando o app reabre, o endereço atual do túnel é publicado no ntfy.sh, **criptografado** com uma chave que só quem tem o código conhece. O nome do canal é um hash do código. O ntfy.sh vê o seu endereço de IP. Veja a [política de privacidade do ntfy.sh](https://ntfy.sh/docs/privacy/). **Pode ser desligado** na instalação ou em Configurações → "Código fixo das salas (ntfy.sh)". Sem ele, o código de uma sala muda sempre que o app reabre. |
| Quando você entra na sala de um amigo pelo código fixo | ntfy.sh e Cloudflare | O app lê no ntfy.sh o endereço da sala e se conecta a ele pela Cloudflare. |
| Ao abrir o app e a cada 6 horas | GitHub (api.github.com) | Consulta qual é a última versão do GameStream, para avisar quando houver atualização. Só lê informação pública; o GitHub vê o seu endereço de IP. **Pode ser desligado** na instalação ou em Configurações → "Avisar quando houver atualização". |

Quem entra na sua sala vê o nome que você escolheu em "Seu nome" e as telas compartilhadas. O app de quem cria a sala vê o endereço de IP de quem entra pelo link (é usado só para o recurso "remover da sala").

O programa usa o Microsoft Edge WebView2 Runtime para mostrar a interface. Ele segue a [política de privacidade da Microsoft](https://privacy.microsoft.com/).

---

## English

GameStream does not collect usage data, has no ads and sends nothing to its authors. There is no account or login. Logs and settings stay on your PC, in `%LOCALAPPDATA%\GameStream`.

The program uses third-party services in these cases:

- **First run – GitHub:** downloads FFmpeg ([BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)) and `cloudflared` ([cloudflare/cloudflared](https://github.com/cloudflare/cloudflared)).
- **While the app is open – Cloudflare Quick Tunnel (trycloudflare.com):** creates the public link of your stream or room. Video, audio and room messages pass through Cloudflare to the viewers. See [Cloudflare's privacy policy](https://www.cloudflare.com/privacypolicy/).
- **If you have saved rooms – ntfy.sh:** so a saved room's code keeps working after the app restarts, the current tunnel address is published to ntfy.sh, **encrypted** with a key only people with the room code know; the channel name is a hash of the code. ntfy.sh sees your IP address. See [ntfy.sh's privacy policy](https://ntfy.sh/docs/privacy/). **This can be turned off** during installation or in Settings → "Código fixo das salas (ntfy.sh)". Without it, a room's code changes every time the app restarts.
- **Joining a friend's room by its fixed code – ntfy.sh and Cloudflare:** the app reads the room address from ntfy.sh and connects to it through Cloudflare.
- **On start and every 6 hours – GitHub (api.github.com):** checks the latest GameStream version to show an update notice. Only public information is read; GitHub sees your IP address. **Can be turned off** during installation or in Settings → "Avisar quando houver atualização".

People in your room see the name you chose and the shared screens. The room owner's app sees the IP address of people who join through the link (used only for "remove from room").

The user interface runs on the Microsoft Edge WebView2 Runtime, which follows [Microsoft's privacy statement](https://privacy.microsoft.com/).
