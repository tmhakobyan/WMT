## Workstation Migration Toolkit 4.2.0-beta

**Ներբեռնեք `WMT-4.2.0-beta.zip` ֆայլը** (ոչ թե «Source code»)։ Բացեք ZIP-ը (աջ սեղմում → Extract All) և կրկնակի սեղմեք `Launch-WMT.cmd`։
Եթե Windows-ը ցույց տա «Windows protected your PC», սեղմեք **More info → Run anyway** (ծրագիրը ստորագրված չէ)։

Ներսում՝ `README.html` (հայերեն օգնություն), `KNOWN-ISSUES.md`, `TEST-REPORT.md` (իրականում գործարկված թեստերի արդյունքներ), `SHA256SUMS.txt`։
`ci-evidence.zip`-ում՝ ավտոմատ GUI թեստի սքրինշոթները։

**Նորություն 4.1-ում.** MSKLC-ով ստեղծված ստեղնաշարի դասավորությունները այլևս ձեռքով վերակառուցելու կարիք չունեն.
ծրագիրը պահում է դրանց DLL-ը և նոր Windows-ում ինքն է տեղադրում ու գրանցում (MSKLC պետք չէ), յուրաքանչյուրի համար
ավտոմատ ստեղծում է `.klc` ֆայլ (Save .klc files, Open in MSKLC), իսկ եթե միայն `.klc` կա և MSKLC-ն տեղադրված է՝
կառուցում է DLL-ը MSKLC-ի կոմպիլյատորով։

Ծրագիրը սկզբում ոչինչ չի սկանավորում և աղբյուր ֆայլերը երբեք չի փոխում։ Windows 10-ը մի ջնջեք, մինչև Readiness-ը ցույց չտա «Migration Ready»։
