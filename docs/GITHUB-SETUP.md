# Ինչպես ստանալ EXE-ն առանց Windows-ում որևէ բան տեղադրելու

1. github.com → New repository (կարող է Private լինել)։
2. Ամբողջ այս թղթապանակի պարունակությունը (ZIP-ը բացած) վերբեռնեք repository-ի արմատ (Add file → Upload files)։ Կարևոր է, որ `.github/workflows/build.yml` ֆայլը հայտնվի իր պանակում։
3. Բացեք Actions ներդիրը → «build» → սպասեք ~5 րոպե։
4. Ավարտից հետո էջի ներքևում՝ Artifacts․ ներբեռնեք `WMT-v3.0.0-m1` (ZIP՝ EXE-ով) և `test-results`։
5. Եթե կարմիր ❌ է՝ բացեք ձախողված քայլը և ինձ ուղարկեք լոգը (կամ screenshot)։ Սա սպասելի է առաջին անգամ, քանի որ կոդը դեռ չի կոմպիլյացվել։
6. Release ZIP-ը բացեք Windows-ում և կրկնակի սեղմեք `Launch-WMT.cmd` (կամ `WMT.exe`)։
