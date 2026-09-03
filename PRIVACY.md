# Privacy Policy

**Applies to:** the SeasonEngine application distributed through the Microsoft Store, and the SeasonEngine open
source libraries.

**Last updated:** 2 September 2026

---

## Summary

SeasonEngine runs on your computer. It has no user accounts, no analytics, no advertising, and no server of ours
that it talks to. Everything you type, record, generate, or open stays on your machine unless you deliberately
export it or share it yourself.

AI generation runs locally on your own CPU and GPU. Your prompts and the content you generate are never
transmitted to us or to anyone else.

We do not operate a backend service, and we do not receive your content, your prompts, or a record of what you do
in the app.

---

## Information we collect

**None.**

We do not collect, transmit, store, or process any personal information about you. Concretely, the application
contains:

- no analytics or telemetry SDK
- no crash or usage reporting
- no advertising SDK or advertising identifier
- no user accounts, sign-in, registration, or profile
- no HTTP client, and no code that sends your data anywhere

We have no server that receives data from the application, so there is nothing for us to collect.

---

## What stays on your device

The application reads and writes the following on your computer only. None of it leaves your machine through the
application.

| Data | Where it lives | Why |
|---|---|---|
| App settings and preferences | Local application data folder | To remember your configuration between sessions |
| Record of your in-app purchases | Local application data folder | To know which features are unlocked |
| Prompts and generation parameters | In memory, and in locally saved results | To perform the generation you asked for |
| Generated text, images, audio, and speech | Local storage folder, and your Downloads folder if you export | These are your files |
| Audio you record for transcription or voice cloning | Local storage, processed locally | To transcribe or to clone a voice as you requested |
| Images and files you open | Read from where you chose them | To generate from, edit, or analyse them |
| Diagnostic logs | Local only, in memory or on disk | To help you troubleshoot; never transmitted |

You can delete any of this by removing the files, clearing the app's local data, or uninstalling the application.

---

## Device permissions

The application may request the following. Each is used only for the stated purpose, only while you are actively
using the corresponding feature, and the captured data is processed locally.

- **Microphone** — to record audio for speech-to-text transcription and for voice cloning from a reference
  recording. Audio is processed on your device by a local model and is never uploaded.
- **Files and folders** — to open images, audio, models, and other files you select, and to save results you
  export. The application accesses only what you choose through a file picker or explicitly save.
- **Photos and media library** — to let you pick existing images or media as input, when you choose to.
- **Screen capture** — the engine includes a screenshot function that captures the application's own window when
  you invoke it. It does not capture your screen in the background and does not capture other applications.

You can decline or revoke any of these in Windows Settings. Declining disables the corresponding feature and
nothing else.

---

## AI processing is local

All AI features — text generation, translation, image generation and editing, music generation, text-to-speech,
voice cloning, speech-to-text, and vision/OCR — run entirely on your own hardware using models stored on your
device.

This means:

- your prompts are not sent to any AI service
- your generated content is not sent anywhere, and is not used to train anything
- your reference audio and input images are not uploaded
- the application works offline once you have the models

Generated content is yours. We have no access to it and no rights over it.

---

## Third parties

We do not share your data with third parties, because we do not have it. There are, however, three points where
you may interact with someone else's service. We want to be precise about them.

### Microsoft Store purchases

Optional in-app purchases are processed entirely by the **Microsoft Store**. We never see or handle your payment
details, and we do not receive your name, address, or payment method.

The application asks the Microsoft Store which add-ons you own, so it knows what to unlock. That exchange is
between the application and Microsoft on your device. Microsoft's handling of your purchase and account data is
governed by the [Microsoft Privacy Statement](https://privacy.microsoft.com/privacystatement).

Microsoft may also provide us with aggregate, anonymised sales and usage statistics through Partner Center. This
does not identify individual users.

### Downloading AI models

The application does not download model files for you. Where a model is required, it links out to the model's page
on **Hugging Face** and opens it in your browser, and you download the files yourself.

Once you follow such a link you are on a third-party website, and that visit is governed by Hugging Face's privacy
policy, not this one. We receive nothing from it. Model weights carry their own licences — check them for your
intended use.

### External links

The application may open other links in your default browser. Once you leave the application, the privacy policy of
the destination site applies.

---

## Windows and your hardware

Windows itself, your graphics drivers, and any GPU runtime you install may collect their own diagnostic data
according to their own policies. That is outside our control and outside this policy. Notably, running AI models
may require you to install vendor components such as CUDA, which are governed by that vendor's terms.

---

## Children

The application is not directed at children and we do not knowingly collect information from anyone, including
children.

Note that the AI features generate content from your prompts using local models, and that generated output is not
filtered or moderated by us. Adults responsible for a shared computer should take that into account.

---

## Your rights

Data protection laws including the GDPR and the CCPA give you rights of access, correction, deletion, and
portability over personal data a company holds about you.

We hold no personal data about you, so there is nothing for us to disclose, correct, export, or delete. Your data
is already entirely in your own hands: it is on your disk, and you can inspect or delete it at any time.

- **We do not sell personal information.** We have none to sell.
- **We do not share personal information for advertising.** There is no advertising in the application.

For purchase records, contact Microsoft, who is the merchant of record.

---

## Security

Your content stays on your device, so its security is governed by your own machine's protections — your Windows
account, disk encryption, and physical access to the computer. We recommend keeping Windows updated and using
full-disk encryption if you generate sensitive content.

The application does not transmit your content, so there is no transmission for anyone to intercept.

---

## Open source

The SeasonEngine core library and the reference application are open source under the MIT License. You can read
exactly what the code does, including everything described in this policy:

**https://github.com/SeasonRealms/SeasonEngine**

We consider this the strongest form of a privacy commitment available: you do not have to take our word for any of
the claims above, because you can verify them in the source.

The SeasonAI layer is commercial source-available software and is not part of the public repository. It follows the
same principle — local processing, no telemetry, no network transmission of your content — and licensed customers
receive its source and can verify this directly.

---

## Changes to this policy

If the application's behaviour changes in a way that affects your privacy, we will update this policy and change
the date at the top. Material changes will be noted in the release notes for the version that introduces them.

Because this policy is kept in a public Git repository, its full revision history is permanently available. You can
see every change ever made to it.

---

## Contact

Questions about this policy or about privacy in SeasonEngine:

- Open an issue: https://github.com/SeasonRealms/SeasonEngine/issues
- Email: `Leming.cen@live.com`
