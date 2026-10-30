# Reference documents

Official and public source documents for grounding GridSim values (grid codes, standards, operating
reports, protection guides, fault-level and network data). Assembled 2026-10-03 from the catalogue in
<https://github.com/MarkS0485/GridSim-GB-Documents> (`sources.csv`, 324 rows) with its `tools/fetch.py`, plus a curl retry.

**Rules**

- A number taken from these documents is cited by document id, version and section in the code comment and the
  CHANGELOG. The catalogue notes (titles, versions, dates) came from web searches: read the title page before
  citing, and correct the row if it is wrong.
- The PDFs are third-party documents whose redistribution terms do not all allow it, so they are NOT in the
  repository: `.gitignore` excludes `docs/reference/**/*.pdf`, and only this catalogue and `MANIFEST.sha256` (the
  SHA-256 of every file as fetched) are tracked. To populate a working copy, run `tools/fetch.py` from the
  [GridSim-GB-Documents](https://github.com/MarkS0485/GridSim-GB-Documents) repository (the `docs/Public/GB`
  submodule) and place the files at the paths below; `MANIFEST.sha256` verifies them. The non-PDF data under
  `etys-pypsa-gb/` (MIT) and `models/` stay tracked.
- `ukgds` (UK Generic Distribution System) is NOT included: SEDG issued no licence (research use only).
- `etys-pypsa-gb/` is from andrewlyden/PyPSA-GB @ 8e084af (MIT, licence file inside): official ETYS Appendix B
  2022-2024, App F and G, FES regional breakdowns, GSP regions, ETYS boundaries, the Strathclyde 29-bus
  GBreducednetwork.m and the reduced / zonal networks.
- `MANIFEST.sha256` lists every file here with its SHA-256.

**Status:** 239 of 324 catalogue rows are here (plus S325, added 2026-10-11 outside the catalogue, N350); 38 are landing pages (need a browser); 47 direct links could not be fetched (reason per row below).

## dcode

| id | title | version | date | publisher | file / status |
|---|---|---|---|---|---|
| S100 | Distribution Code + Guide | Issue 24 | ~2014 | DCode | [S100-D_Code_Issue_24.pdf](dcode/S100-D_Code_Issue_24.pdf) |
| S101 | Distribution Code + Guide | Issue 25 | ~2015 | DCode | [S101-D_Code_Issue_25.pdf](dcode/S101-D_Code_Issue_25.pdf) |
| S102 | Distribution Code | Issue 36 | 2018-12-06 | DCode | [S102-D_Code_36_clean_181206.pdf](dcode/S102-D_Code_36_clean_181206.pdf) |
| S103 | Distribution Code | Issue 45 | 2020-06-12 | DCode | [S103-DCode_v45_20200612.pdf](dcode/S103-DCode_v45_20200612.pdf) |
| S104 | Distribution Code | Issue 55 | 2023-12-15 | DCode | [S104-DCode_v55_231215.pdf](dcode/S104-DCode_v55_231215.pdf) |
| S105 | DCode archive |  |  | DCode | landing page: <https://dcode.org.uk/dcode-archive.html> |
| S106 | DCode Annex 1 (qualifying standards) |  |  | DCode | landing page: <https://dcode.org.uk/annexes/annex-1/> |

## ena

| id | title | version | date | publisher | file / status |
|---|---|---|---|---|---|
| S124 | EREC G99 | Issue 2 | 2025-03-10 | ENA (DCode) | [S124-250307ena-erec-g99-issue-2-_2025_.pdf](ena/S124-250307ena-erec-g99-issue-2-_2025_.pdf) |
| S125 | EREC G99 | Issue 1 Amd 10 | 2024 | ENA (DCode) | [S125-ENA_EREC_G99_Issue_1_Amendment_10__2024_.pdf](ena/S125-ENA_EREC_G99_Issue_1_Amendment_10__2024_.pdf) |
| S126 | EREC G99 | Issue 1 Amd 9 | 2022 | ENA (DCode) | [S126-ENA_EREC_G99_Issue_1_Amendment_9__2022_.pdf](ena/S126-ENA_EREC_G99_Issue_1_Amendment_9__2022_.pdf) |
| S127 | EREC G99 | Issue 1 Amd 5 | 2019 | ENA (DCode) | [S127-ENA_EREC_G99_Issue_1_Amendment_5__2019_.pdf](ena/S127-ENA_EREC_G99_Issue_1_Amendment_5__2019_.pdf) |
| S128 | EREC G99 | Issue 1 Amd 1 | 2018 | ENA (DCode) | [S128-ENA_EREC_G99_Issue_1_Ammendment_1_2018_1.pdf](ena/S128-ENA_EREC_G99_Issue_1_Ammendment_1_2018_1.pdf) |
| S129 | EREC G99/NI | Issue 2 | 2026-05 | NIE Networks | [S129-ENA-EREC-G99-NI-Issue-2.pdf](ena/S129-ENA-EREC-G99-NI-Issue-2.pdf) |
| S130 | EREC G98 | Issue 2 | 2025-03-10 | ENA (DCode) | [S130-250307ena-erec-g98-issue-2-_2025_.pdf](ena/S130-250307ena-erec-g98-issue-2-_2025_.pdf) |
| S131 | EREC G98 | Issue 1 Amd 7 | 2022-10 | ENA (DCode) | [S131-ENA_EREC_G98_Issue_1_Amendment_7__2022_.pdf](ena/S131-ENA_EREC_G98_Issue_1_Amendment_7__2022_.pdf) |
| S132 | EREC G59 | Issue 3 Amd 2 | 2015 | ENA (DCode) | [S132-ENA_EREC_G59_Issue_3_Amendment_2__2015_.pdf](ena/S132-ENA_EREC_G59_Issue_3_Amendment_2__2015_.pdf) |
| S133 | EREC G59 | Issue 3 Amd 6 | 2019 | ENA (DCode) | [S133-ENA_EREC_G59_Issue_3_Amendment_6__2019_.pdf](ena/S133-ENA_EREC_G59_Issue_3_Amendment_6__2019_.pdf) |
| S134 | EREC G59 | Issue 3 Amd 7 | 2019-09 | ENA (DCode) | [S134-ENA_EREC_G59_Issue_3_Amendment_7__2019_.pdf](ena/S134-ENA_EREC_G59_Issue_3_Amendment_7__2019_.pdf) |
| S135 | EREC G59 | Issue 3 Amd 8 | 2024 | ENA (DCode) | [S135-250521ena-erec-g59-issue-3-amendment-8-_2024_.pdf](ena/S135-250521ena-erec-g59-issue-3-amendment-8-_2024_.pdf) |
| S136 | G59/2-1 extract | Issue 2 | 2011-04 | ENA | [S136-ENA_EREC_G59_2_Extract_180902050624.pdf](ena/S136-ENA_EREC_G59_2_Extract_180902050624.pdf) |
| S137 | EREC G83 | Issue 2 Amd 3 | 2019 | ENA (DCode) | [S137-ENA_EREC_G83_Issue_2_Amendment_3__2019_.pdf](ena/S137-ENA_EREC_G83_Issue_2_Amendment_3__2019_.pdf) |
| S138 | EREC G100 export limitation | Issue 2 Amd 2 | 2023 | ENA (ENWL copy) | [S138-g100_a2_457461.pdf](ena/S138-g100_a2_457461.pdf) |
| S139 | EREC G5 harmonics (its list of tables names the emission table "Table B.1 -- Harmonic current emission assumptions for three-phase equipment types" at p. 99; the table itself is headed "Table B.3" (pp. 99-100): cite both, N133) | Issue 5 | 2020 | ENA (DCode) | [S139-ENA_EREC_G5_Issue_5__2020_.pdf](ena/S139-ENA_EREC_G5_Issue_5__2020_.pdf) |
| S140 | ER G5/4-1 | Issue 4 Amd 1 | 2005 | ENA (DCode) | [S140-ENA_ER_G5_Issue_4_Amendment_1__2005_.pdf](ena/S140-ENA_ER_G5_Issue_4_Amendment_1__2005_.pdf) |
| S141 | EREC P28 voltage fluctuations | Issue 2 | 2019 | ENA (DCode) | [S141-ENA_EREC_P28_Issue_2__2019_.pdf](ena/S141-ENA_EREC_P28_Issue_2__2019_.pdf) |
| S142 | EREC P2 security of supply | Issue 8 | 2023 | ENA (DCode) | [S142-ENA_EREC_P2_Issue_8__2023_.pdf](ena/S142-ENA_EREC_P2_Issue_8__2023_.pdf) |
| S143 | EREP 130 P2 application guidance | Issue 4 | 2023 | ENA (DCode) | [S143-ENA_EREP_130_Issue_4__2023_.pdf](ena/S143-ENA_EREP_130_Issue_4__2023_.pdf) |
| S144 | EREC P25 LV fault level | Issue 2 | 2018 | ENA (DCode) | [S144-ENA_EREC_P25_Issue_2__2018__3.pdf](ena/S144-ENA_EREC_P25_Issue_2__2018__3.pdf) |
| S145 | G5/5 vs G5/4 comparison |  |  | Outram/EIL | [S145-G5_5-vs-G5_4-summary-report-EIL-for-Outram.pdf](ena/S145-G5_5-vs-G5_4-summary-report-EIL-for-Outram.pdf) |

## etys

| id | title | version | date | publisher | file / status |
|---|---|---|---|---|---|
| S239 | ETYS documents and appendices (2023-2025) |  |  | NESO | landing page: <https://www.neso.energy/publications/electricity-ten-year-statement-etys/etys-documents-and-appendices> |
| S240 | ETYS 2022 documents and appendices |  |  | NG ESO | landing page: <https://www.nationalgrideso.com/research-and-publications/electricity-ten-year-statement-etys/etys-2022-documents-and-appendices> |
| S241 | ETYS archive (2016-2021) |  |  | NG ESO | landing page: <https://www.nationalgrideso.com/research-and-publications/electricity-ten-year-statement-etys/etys-archive> |
| S242 | ETYS fault level narrative App D | ETYS22 | 2023-01 | NESO | [S242-2022_Appendix_D_-_Fault_Level_Narrative.pdf](etys/S242-2022_Appendix_D_-_Fault_Level_Narrative.pdf) |
| S243 | ETYS fault level narrative App D | ETYS21 | 2021-11 | NESO | [S243-Appendix_D_-_Fault_Level_Narrative.pdf](etys/S243-Appendix_D_-_Fault_Level_Narrative.pdf) |
| S244 | ETYS fault level narrative App D | ETYS20 | 2020-11 | NESO | [S244-Appendix_D_0.pdf](etys/S244-Appendix_D_0.pdf) |
| S245 | ETYS 2015 App D fault levels | 2015 | 2015 | National Grid | [S245-43745-ETYS15_AppD_fault_level_intro.pdf](etys/S245-43745-ETYS15_AppD_fault_level_intro.pdf) |
| S246 | ETYS 2017 App D narrative | 2017 | 2017-11 | National Grid | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/ETYS%202017%20Appendix%20D%20Narrative.pdf> |
| S247 | Provision of short circuit level data |  | 2022-02 | NESO | [S247-Provision_of_short_circuit_level_data_SOF_paper_Feb_2022.pdf](etys/S247-Provision_of_short_circuit_level_data_SOF_paper_Feb_2022.pdf) |
| S248 | ETYS 2015 main document | 2015 | 2015-11 | National Grid | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/44084-ETYS%202015%20for%20Web%20.pdf> |
| S249 | ETYS 2017 all chapters | 2017 | 2017-11 | National Grid | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/14843_NG_ETYS_2017_AllChapters_A01_INT.pdf> |
| S250 | ETYS 2024 structure document | 2024 | 2024 | NESO | [S250-ETYS_Consultation_2024.pdf](etys/S250-ETYS_Consultation_2024.pdf) |
| S251 | SSEN Transmission ETYS24 fault levels (dataset) | 2024/25 |  | SSEN Transmission | landing page: <https://ssentransmission.opendatasoft.com/explore/dataset/etys-24-yr-01-she-transmission-fault-levels-ka-summer-2024-25/> |

## events

| id | title | version | date | publisher | file / status |
|---|---|---|---|---|---|
| S188 | Ofgem 9 Aug 2019 power outage report | final | 2020-01-03 | Ofgem | [S188-9_august_2019_power_outage_report.pdf](events/S188-9_august_2019_power_outage_report.pdf) |
| S189 | ESO technical report 9 Aug 2019 | final | 2019-09-06 | NG ESO | [S189-eso_technical_report_-_final.pdf](events/S189-eso_technical_report_-_final.pdf) |
| S190 | ESO technical report 9 Aug 2019 (NESO copy) | final | 2019-09-06 | NG ESO | [S190-ESO_Technical_Report_-_FINAL2.pdf](events/S190-ESO_Technical_Report_-_FINAL2.pdf) |
| S191 | ESO technical report 9 Aug 2019 appendices | final | 2019-09-06 | NG ESO | [S191-eso_technical_report_-_appendices_-_final.pdf](events/S191-eso_technical_report_-_appendices_-_final.pdf) |
| S192 | ESO interim LFDD report 9 Aug 2019 | interim | 2019-08-16 | NG ESO | [S192-national_grid_eso_report_lfdd_9_august_2019.pdf](events/S192-national_grid_eso_report_lfdd_9_august_2019.pdf) |
| S193 | ESO interim LFDD report (NESO copy) |  |  | NG ESO | [S193-ESO_Interim_technical_report_0.pdf](events/S193-ESO_Interim_technical_report_0.pdf) |
| S194 | E3C final report 9 Aug 2019 | final | 2020-01 | BEIS | [S194-e3c-gb-power-disruption-9-august-2019-final-report.pdf](events/S194-e3c-gb-power-disruption-9-august-2019-final-report.pdf) |
| S195 | E3C interim report | interim | 2019-10 | BEIS | [S195-20191003_E3C_Interim_Report_into_GB_Power_Disruption.pdf](events/S195-20191003_E3C_Interim_Report_into_GB_Power_Disruption.pdf) |
| S196 | NG frequency deviation report (likely 27 May 2008) |  | 2009-02 | National Grid | [S196-Report_into_System_Events_of_27_May_2008.pdf](events/S196-Report_into_System_Events_of_27_May_2008.pdf) |
| S197 | NG system events of 27 May 2008 (DSWG) |  | 2008-07 | NG via Ofgem | [S197-nationalgrid-systemeventsof27mayfordswg16july.pdf](events/S197-nationalgrid-systemeventsof27mayfordswg16july.pdf) |
| S198 | Operational Transparency Forum slides |  | 2024-06-19 | NESO | [S198-ESO_Transparency_Forum_24-06-19_vFinal.pdf](events/S198-ESO_Transparency_Forum_24-06-19_vFinal.pdf) |

## fault-levels

| id | title | version | date | publisher | file / status |
|---|---|---|---|---|---|
| S252 | NGED LTDS tabular model (fault level CSV) |  | 2025-12 | NGED | landing page: <https://connecteddata.nationalgrid.co.uk/dataset/ltds-tabular-model> |
| S253 | NGED LTDS CIM |  |  | NGED | landing page: <https://connecteddata.nationalgrid.co.uk/dataset/ltds-common-information-model> |
| S254 | NGED FlexDGrid fault level monitor data |  |  | NGED | landing page: <https://connecteddata.nationalgrid.co.uk/dataset/flexdgrid-fault-level-monitor-data> |
| S255 | SPD LTDS appendix 4 fault levels | 2025 |  | SPEN | landing page: <https://spenergynetworks.opendatasoft.com/explore/assets/spd-ltds-appendix-4-fault-levels-table-4/> |
| S256 | SPM LTDS appendix 4 132 kV 1ph fault levels | 2025 |  | SPEN | landing page: <https://spenergynetworks.opendatasoft.com/explore/assets/spm-ltds-appendix-4-fault-levels-table-4-132kv-1ph/> |
| S257 | SPD LTDS |  | 2025-11 | SPEN | not fetched: blocked (HTTP 403, needs a browser): <https://www.spenergynetworks.co.uk/userfiles/file/SPD_Long_Term_Development_Statement_Nov_2025.pdf> |
| S258 | SPM LTDS |  | 2025-11 | SPEN | not fetched: blocked (HTTP 403, needs a browser): <https://www.spenergynetworks.co.uk/userfiles/file/SPM_Long_Term_Development_Statement_Nov_2025.pdf> |
| S259 | UKPN grid and primary fault level data |  |  | UKPN | landing page: <https://ukpowernetworks.opendatasoft.com/explore/dataset/grid-and-primary-sites-fault-level-data/> |
| S260 | UKPN LTDS table 8 fault data |  |  | UKPN/Open Net Zero | landing page: <https://opennetzero.org/dataset/ltds-table-8-gt-95-perc-fault-data> |
| S261 | SEPD LTDS parts 1 & 2 |  | 2023-11 | SSEN | [S261-southern-electric-power-distribution-long-term-development-statement-part-1--2-november-2023-.pdf](fault-levels/S261-southern-electric-power-distribution-long-term-development-statement-part-1--2-november-2023-.pdf) |
| S262 | SHEPD LTDS parts 1 & 2 |  |  | SSEN | [S262-scottish-hydro-electric-power-distribution-long-term-development-statement_parts-12.pdf](fault-levels/S262-scottish-hydro-electric-power-distribution-long-term-development-statement_parts-12.pdf) |
| S263 | NPg LTDS appendix 6 fault levels |  |  | Northern Powergrid | landing page: <https://northernpowergrid.opendatasoft.com/explore/dataset/ltds-appendix-6/> |
| S264 | ENWL LTDS |  | 2025-11 | ENWL | landing page: <https://www.enwl.co.uk/get-connected/network-information/long-term-development-statement/> |

## frcr

| id | title | version | date | publisher | file / status |
|---|---|---|---|---|---|
| S041 | FRCR landing page (all editions, methodology, data handbook) |  |  | NESO | landing page: <https://www.neso.energy/industry-information/codes/security-and-quality-supply-standard-sqss/frequency-risk-and-control-report-frcr> |
| S042 | FRCR 2021 (first FRCR) | 2021 | 2021-04-01 | NG ESO | [S042-FRCR_Report_2021-04_Apr_v2.pdf](frcr/S042-FRCR_Report_2021-04_Apr_v2.pdf) |
| S043 | SQSS Frequency Risk and Control Policy |  | 2020/21 | NG ESO | [S043-Annex_2_-_Frequency_Risk_and_Control_Policy.pdf](frcr/S043-Annex_2_-_Frequency_Risk_and_Control_Policy.pdf) |
| S044 | SQSS FRCR (earlier ID, draft/consultation?) |  | 2020/21 | NG ESO | [S044-Annex_1_-_Frequency_Risk_and_Control_Report_Methodology.pdf](frcr/S044-Annex_1_-_Frequency_Risk_and_Control_Report_Methodology.pdf) |
| S045 | FRCR (probably 2022) | 2022? | 2022-04 | NG ESO | [S045-FRCR_2022_Report_and_methodology_Consultation.pdf](frcr/S045-FRCR_2022_Report_and_methodology_Consultation.pdf) |
| S046 | FRCR (probably 2023) | 2023? |  | NG ESO | [S046-Frequency_Risk_and_Control_Report_and_Methodology_2024_for_consultation.pdf](frcr/S046-Frequency_Risk_and_Control_Report_and_Methodology_2024_for_consultation.pdf) |
| S047 | FRCR and Methodology 2024 | 2024 | 2024-07-12 | NG ESO | [S047-Frequency_Risk_and_Control_Report_and_Methodology_2024_For_Approval__v2.pdf](frcr/S047-Frequency_Risk_and_Control_Report_and_Methodology_2024_For_Approval__v2.pdf) |
| S048 | FRCR 2024 consultation webinar slides | 2024 |  | NG ESO | [S048-FRCR2024_Webinar_slide.pdf](frcr/S048-FRCR2024_Webinar_slide.pdf) |
| S049 | Public FRCR 2025 (consultation version) | 2025 |  | NESO | [S049-Frequency_Risk_and_Control_Report_2025.pdf](frcr/S049-Frequency_Risk_and_Control_Report_2025.pdf) |
| S050 | Webinar: pre-fault frequency control modelling | 2025 | 2025-03-12 | NESO | [S050-Pre-fault_frequency_control_modelling_webinar.pdf](frcr/S050-Pre-fault_frequency_control_modelling_webinar.pdf) |
| S051 | FRCR 2025 engineering assurance | 2025 |  | NESO | [S051-FRCR_2025_Integrated_technical_review_final_landscape_website_v2.pdf](frcr/S051-FRCR_2025_Integrated_technical_review_final_landscape_website_v2.pdf) |
| S052 | FRCR 2025, the report (the cover reads "The Report - For Approval ... May 2025"; the file name says "For Submission", N133) | 2025 | 2025-05-16 | NESO | [S052-Frequency_Risk_and_Control_Report_2025_For_Submission.pdf](frcr/S052-Frequency_Risk_and_Control_Report_2025_For_Submission.pdf) |
| S053 | FRCR methodology v3 (the cover reads "The Methodology - May 2025 (v3)"; the page header and the version table read "March 2025 (v3)", N133) | v3 | 2025-05 (cover); 2025-03 (header) | NESO | [S053-FRCR_Methodology.pdf](frcr/S053-FRCR_Methodology.pdf) |
| S054 | FRCR 2025 data handbook | 2025 | 2025-05 | NESO | [S054-FRCR_Data_Handbook.pdf](frcr/S054-FRCR_Data_Handbook.pdf) |
| S055 | FRCR 2025 supplementary report | 2025 | 2026-03 | NESO | [S055-FRCR_2025_supplementary_report_For_submission_.pdf](frcr/S055-FRCR_2025_supplementary_report_For_submission_.pdf) |
| S056 | SQSS panel minutes reviewing FRCR 2025 |  | 2025-05-06 | NESO | [S056-SQSS_Panel_Meeting_Minutes_-_06_May_2025.pdf](frcr/S056-SQSS_Panel_Meeting_Minutes_-_06_May_2025.pdf) |
| S057 | Ofgem decision approving FRCR 2021 | 2021 | 2021-05 | Ofgem | [S057-authority_decision_to_approve_the_frequency_risk_and_control_report_april_2021.pdf](frcr/S057-authority_decision_to_approve_the_frequency_risk_and_control_report_april_2021.pdf) |
| S058 | Ofgem FRCR 2022 authority decision | 2022 | 2022-06 | Ofgem | [S058-FRCR_2022_Authority_Decision.pdf](frcr/S058-FRCR_2022_Authority_Decision.pdf) |
| S059 | Ofgem FRCR 2023 authority decision | 2023 | 2023-06-09 | Ofgem | [S059-FRCR_2023_Authority_Decision1686224869212.pdf](frcr/S059-FRCR_2023_Authority_Decision1686224869212.pdf) |
| S060 | Ofgem FRCR 2024 authority decision | 2024 | 2024-09-27 | Ofgem | [S060-FRCR_2024_Authority_Decision.pdf](frcr/S060-FRCR_2024_Authority_Decision.pdf) |
| S061 | Ofgem FRCR 2025 consultation | 2025 | 2025-08 | Ofgem | [S061-Frequency_Risk_and_Control_Report_2025_consultation.pdf](frcr/S061-Frequency_Risk_and_Control_Report_2025_consultation.pdf) |
| S062 | Ofgem FRCR 2025 authority decision (not approved) | 2025 | 2025-12 | Ofgem | [S062-FRCR-2025-Authority-Decision.pdf](frcr/S062-FRCR-2025-Authority-Decision.pdf) |
| S063 | FRCR 2025 independent review | 2025 | 2026-03 | Ofgem | [S063-Frequency_Risk_and_Control_Report_2025_independent_review.pdf](frcr/S063-Frequency_Risk_and_Control_Report_2025_independent_review.pdf) |
| S064 | SQSS FRCR 2026 authority decision (2026 report suspended) | 2026 | 2026-03 | Ofgem | [S064-SQSS_FRCR_2026_Authority_Decision.pdf](frcr/S064-SQSS_FRCR_2026_Authority_Decision.pdf) |

## frequency

| id | title | version | date | publisher | file / status |
|---|---|---|---|---|---|
| S199 | System inertia dataset |  |  | NESO | landing page: <https://www.neso.energy/data-portal/system-inertia> |
| S200 | System inertia cost dataset |  |  | NESO | landing page: <https://www.neso.energy/data-portal/system-inertia-cost> |
| S201 | Operability Strategy Report |  | 2023-12 | NG ESO | [S201-ESO_Operability_Report_2023-24.pdf](frequency/S201-ESO_Operability_Report_2023-24.pdf) |
| S202 | Operability Strategy Report |  | 2026-03 | NESO | [S202-742206_NESO_Operability_Strategy_Report_2026_V6.pdf](frequency/S202-742206_NESO_Operability_Strategy_Report_2026_V6.pdf) |
| S203 | Operability Strategy Report |  | 2022-12 | NG ESO | not fetched: host did not respond: <https://www.nationalgrideso.com/document/273801/download> |
| S204 | Operability Strategy Report (early edition) |  | 2018-21 | NG ESO | [S204-Operability_Strategy_Report_Final.pdf](frequency/S204-Operability_Strategy_Report_Final.pdf) |
| S205 | System Operability Framework 2016 |  | 2016-11 | National Grid | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/8589937802-SOF%202016%20-%20Full%20Non-interactive%20Document.pdf> |
| S206 | System Operability Framework 2014 |  | 2014 | National Grid | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/35435-SOF%202014%20Final.pdf> |
| S207 | SOF summary |  | ~2016 | National Grid | [S207-44066-SOF_2015_Summary.pdf](frequency/S207-44066-SOF_2015_Summary.pdf) |
| S208 | Frequency Response Technical Sub-Group report | v1.0 | 2011-11-15 | NGET | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/28745-Final%20Technical%20Sub%20Group%20Report.pdf> |
| S209 | Frequency Response WG report | v1.0 | 2013-01-09 | NGET | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/16927-Workgroup%20Report.pdf> |
| S210 | Frequency response obligations |  | ~2013 | National Grid | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/16889-Frequency%20Response%20Obligations.pdf> |
| S211 | Frequency response obligations (NESO copy) |  |  | National Grid | [S211-16889-Frequency_Response_Obligations.pdf](frequency/S211-16889-Frequency_Response_Obligations.pdf) |
| S212 | EFCC final document |  | 2019-05-01 | NG ESO | [S212-EFCC_May19_Slides.pdf](frequency/S212-EFCC_May19_Slides.pdf) |
| S213 | EFCC progress report |  | 2016-17 | National Grid | [S213-NG_NIC_EFCC_6_Monthly_Report_January_June_2016_0.pdf](frequency/S213-NG_NIC_EFCC_6_Monthly_Report_January_June_2016_0.pdf) |
| S214 | EFCC progress report 2 |  | 2016-17 | National Grid | [S214-NIC_EFCC_6_Monthly_Progress_Report_0.pdf](frequency/S214-NIC_EFCC_6_Monthly_Progress_Report_0.pdf) |
| S215 | EFCC NIC submission |  |  | National Grid | [S215-LCNI_2016_Presentation.pdf](frequency/S215-LCNI_2016_Presentation.pdf) |
| S216 | Stability Pathfinder phase 1 FAQ |  | 2019-12 | NG ESO | [S216-FAQ_Stability_Phase_1_Tender_Pack_v1.1_1.pdf](frequency/S216-FAQ_Stability_Phase_1_Tender_Pack_v1.1_1.pdf) |
| S217 | Stability Pathfinder phase 2 assessment methodology |  | 2020-21 | NG ESO | [S217-Stability_Phase_2_Assessment_Methodology.pdf](frequency/S217-Stability_Phase_2_Assessment_Methodology.pdf) |
| S218 | Stability Pathfinder phase 2 EOI summary |  |  | NG ESO | [S218-Stability_Pathfinder_Phase_2_-_EOI_summary.pdf](frequency/S218-Stability_Pathfinder_Phase_2_-_EOI_summary.pdf) |
| S219 | Stability Pathfinder phase 2 FAQ |  | 2021-10 | NG ESO | [S219-FAQ_update_041021_0.pdf](frequency/S219-FAQ_update_041021_0.pdf) |
| S220 | Stability Pathfinder phase 3 tender overview |  | 2021-22 | NG ESO | [S220-1._StabilityPhase3_Tender_Overview.pdf](frequency/S220-1._StabilityPhase3_Tender_Overview.pdf) |
| S221 | Stability Pathfinder phase 3 technical feasibility |  |  | NG ESO | [S221-8._Stability_Phase_3_Technical_Feasibility_Study_Guidance_V1.pdf](frequency/S221-8._Stability_Phase_3_Technical_Feasibility_Study_Guidance_V1.pdf) |
| S222 | Stability Pathfinder phase 3 regions of need |  |  | NG ESO | [S222-07._Stability_Phase_3_Network_Diagram_Map_V2.pdf](frequency/S222-07._Stability_Phase_3_Network_Diagram_Map_V2.pdf) |
| S223 | Stability technical performance requirements v3 | v3 | 2020-11 | NG ESO | [S223-3._StabilityPhase3_Technical_Specification_V1.pdf](frequency/S223-3._StabilityPhase3_Technical_Specification_V1.pdf) |
| S224 | Stability deep dive slides |  | 2023-03-28 | NG ESO | [S224-Stability_deep_dive_PDF_2_.pdf](frequency/S224-Stability_deep_dive_PDF_2_.pdf) |
| S225 | Dynamic Containment service terms |  | 2020 | NG ESO | [S225-Dynamic_Containment_Test_Guidance_-_draft_version.pdf](frequency/S225-Dynamic_Containment_Test_Guidance_-_draft_version.pdf) |
| S226 | DC guidance |  | 2020 | NG ESO | [S226-DC_Guidance_Document_Consultation_Draft_21.08.20.pdf](frequency/S226-DC_Guidance_Document_Consultation_Draft_21.08.20.pdf) |
| S227 | DC testing guidelines |  | 2020 | NG ESO | [S227-DC_Testing_Guidelines_Consultation_Draft_21.08.20.pdf](frequency/S227-DC_Testing_Guidelines_Consultation_Draft_21.08.20.pdf) |
| S228 | DC launch Jan 2020 |  | 2020 | NG ESO | [S228-Dynamic_Containment.pdf](frequency/S228-Dynamic_Containment.pdf) |
| S229 | DC FAQs |  | 2020 | NG ESO | [S229-Dynamic_Containment_FAQs.pdf](frequency/S229-Dynamic_Containment_FAQs.pdf) |
| S230 | Dynamic response services provider guidance | v13 | 2025-10 | NESO | [S230-Response_Services_-_Provider_Guidance_V14.pdf](frequency/S230-Response_Services_-_Provider_Guidance_V14.pdf) |
| S231 | DM test guidance |  | ~2022 | NG ESO | [S231-Dynamic_Moderation_Testing_Guidelines_v1.0.pdf](frequency/S231-Dynamic_Moderation_Testing_Guidelines_v1.0.pdf) |
| S232 | DR test guidance |  | ~2022 | NG ESO | [S232-Dynamic_Regulation_Testing_Guidelines_v1.0.pdf](frequency/S232-Dynamic_Regulation_Testing_Guidelines_v1.0.pdf) |
| S233 | DM/DR technical workshop output |  | 2021 | NG ESO | [S233-DM_DR_technical_workshops_output_summary_slides.pdf](frequency/S233-DM_DR_technical_workshops_output_summary_slides.pdf) |
| S234 | Mandatory frequency response |  | ~2017 | NG ESO | [S234-Mandatory_Frequency_Response_Guide_v1.1.pdf](frequency/S234-Mandatory_Frequency_Response_Guide_v1.1.pdf) |
| S235 | Frequency response technical guidance and testing |  |  | National Grid | [S235-DSR_Battery_Storage_Test_Procedure_for_Frequency_Response.pdf](frequency/S235-DSR_Battery_Storage_Test_Procedure_for_Frequency_Response.pdf) |
| S236 | Firm Frequency Response |  |  | National Grid | [S236-FFR_FAQs_v1.3.pdf](frequency/S236-FFR_FAQs_v1.3.pdf) |
| S237 | EFR FAQs v5.0 | v5.0 | 2016-03 | National Grid | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/Enhanced%20Frequency%20Response%20FAQs%20v5.0_.pdf> |
| S238 | LFC 2024 annual report |  | 2025-09-30 | NESO | [S238-LFC_Annual_Report_2024_0.pdf](frequency/S238-LFC_Annual_Report_2024_0.pdf) |

## grid-code

| id | title | version | date | publisher | file / status |
|---|---|---|---|---|---|
| S065 | GB Grid Code at BETTA go-active | Issue 3 Rev 0 | 2004-08 | Ofgem/NGT | [S065-7885-grid_code_betta04b_0.pdf](grid-code/S065-7885-grid_code_betta04b_0.pdf) |
| S066 | GB Grid Code operating codes OC1/2/6/7/9/10/12 consultation | draft | 2003-10-31 | Ofgem | [S066-5018-gb_gridcode_operating_codes_31oct03.pdf](grid-code/S066-5018-gb_gridcode_operating_codes_31oct03.pdf) |
| S067 | GB Grid Code PC/DRC consultation | draft | 2004-02 | Ofgem | [S067-6075-3704.pdf](grid-code/S067-6075-3704.pdf) |
| S068 | Grid Code update pack | Issue 3 Rev 26 | 2008-04-01 | NGET | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/29448-Issue%203%20Revision%2026%20-%201%20April%202008.pdf> |
| S069 | Grid Code update pack | Issue 4 Rev 2 | 2010-03-22 | NGET | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/29460-Issue%204%20Revision%202%20-%2022%20March%202010.pdf> |
| S070 | Grid Code Connection Conditions | Issue 5 Rev 21 | 2017-03-21 | NGET | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/8589935284-Connection%20Conditions.pdf> |
| S071 | Full Grid Code | Issue 5 Rev 20 | 2017-02-20 | NGET | [S071-8589938962-Issue_5_Revision_20_-_20_February_2017.pdf](grid-code/S071-8589938962-Issue_5_Revision_20_-_20_February_2017.pdf) |
| S072 | European Connection Conditions | Issue 5 Rev 25 | 2018-09-07 | NGET/ESO | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/06_EUROPEAN_CONNECTION_CONDITIONS_I5R25.pdf> |
| S073 | Full Grid Code | Issue 5 Rev 29 | 2018-11-01 | NG ESO | [S073-Update_Pack_Issue_5_Revision_29_V2_0.pdf](grid-code/S073-Update_Pack_Issue_5_Revision_29_V2_0.pdf) |
| S074 | Grid Code CC section | Issue 5 Rev 33 | 2019-04-05 | NG ESO | [S074-CONNECTION_CONDITIONS_-_Cut_down_for_GC0123.pdf](grid-code/S074-CONNECTION_CONDITIONS_-_Cut_down_for_GC0123.pdf) |
| S075 | Grid Code OC5 | Issue 5 Rev 15 | 2016-02-03 | NGET | [S075-OC5_Testing_and_Monitoring.pdf](grid-code/S075-OC5_Testing_and_Monitoring.pdf) |
| S076 | Full Grid Code (ENTSO-E mirror) | Issue 6 Rev 16 | 2023-01-05 | NG ESO | [S076-uk_00_FULL_GRID_CODE_I6R16.pdf](grid-code/S076-uk_00_FULL_GRID_CODE_I6R16.pdf) |
| S077 | The Grid Code (full) | Issue 6 Rev ? | ~2021-22 | NG ESO | [S077-GC0151_Update_Pack_I6R8_1.pdf](grid-code/S077-GC0151_Update_Pack_I6R8_1.pdf) |
| S078 | Grid Code OC6 demand control | Issue 6 Rev 20 | 2023-12-15 | NG ESO | [S078-14_Operating_Code_6_I6R20_1.pdf](grid-code/S078-14_Operating_Code_6_I6R20_1.pdf) |
| S079 | Serviced Grid Code | Issue 6 Rev ~35 | 2025-12-04 | NESO | [S079-GC0183_Update_Pack_I6R35.pdf](grid-code/S079-GC0183_Update_Pack_I6R35.pdf) |
| S080 | Serviced Grid Code | Issue 6 Rev ~40 | 2026-05-28 | NESO | [S080-Establishing_a_harmonised_prioritisation_process_in_the_Industry_Codes_Update_Pack_I6R40_1_.pdf](grid-code/S080-Establishing_a_harmonised_prioritisation_process_in_the_Industry_Codes_Update_Pack_I6R40_1_.pdf) |
| S081 | Serviced Grid Code (alternate ID) | Issue 6 | 2026 | NESO | [S081-EDL_Message_Interface_Specification_Update_Pack_I6R37.pdf](grid-code/S081-EDL_Message_Interface_Specification_Update_Pack_I6R37.pdf) |
| S082 | Serviced Grid Code (latest found) | Issue 6 Rev 45 | 2026-08-13 | NESO | [S082-gc0176-update-pack-i6r45.pdf](grid-code/S082-gc0176-update-pack-i6r45.pdf) |
| S083 | Grid Code documents (all issues/revisions) |  |  | NESO | landing page: <https://www.neso.energy/industry-information/codes/grid-code-gc/grid-code-documents> |
| S084 | Data Registration Code | Issue 5 Rev 15 | 2016-02 | National Grid | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/Data%20Registration%20Code.pdf> |
| S085 | Guidance notes for generators: submission of Grid Code data |  |  | NESO | [S085-32083-Guidance_Notes_for_Generators_-_Submission_of_Grid_Code_Data.pdf](grid-code/S085-32083-Guidance_Notes_for_Generators_-_Submission_of_Grid_Code_Data.pdf) |
| S086 | Guidance notes for synchronous generators |  | 2024-04 | NESO | [S086-Guidance_Notes_for_Synchronous_Generators_Issue_3_EU_Code_User_.pdf](grid-code/S086-Guidance_Notes_for_Synchronous_Generators_Issue_3_EU_Code_User_.pdf) |
| S087 | LFDD relay settings paper |  | ~2010s | NGET | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/15249-Low%20Frequency%20Demand%20Disconnection%20Relay%20Settings.pdf> |
| S088 | GCRP review of OC6.6 WG final report |  | ~2012 | NGET | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/15453-OC6%206%20WG%20Final%20Report.pdf> |
| S089 | EU NCER System Defence Plan | Issue 6 | 2025-04 | NESO | [S089-System_Defense_Plan_V6.pdf](grid-code/S089-System_Defense_Plan_V6.pdf) |
| S090 | GC0105 FMR (system incidents reporting) |  | 2016 | NGET | [S090-Workgroup_Report_Final_11_April_2019_0.pdf](grid-code/S090-Workgroup_Report_Final_11_April_2019_0.pdf) |
| S091 | GC0105 (alternate ID) |  |  | NGET | [S091-Workgroup_Report_-_25_September_2019.pdf](grid-code/S091-Workgroup_Report_-_25_September_2019.pdf) |
| S092 | GC0105 proposal |  |  | NGET | [S092-Guy_N_System_Incidents_Reports_presentation_0.pdf](grid-code/S092-Guy_N_System_Incidents_Reports_presentation_0.pdf) |
| S093 | GC0137 final modification report (GB grid forming) |  | 2021-11-11 | NG ESO | [S093-GC0137_Final_Modification_Report.pdf](grid-code/S093-GC0137_Final_Modification_Report.pdf) |
| S094 | GC0137 FMR (19 Oct 2021 version) |  | 2021-10-19 | NG ESO | [S094-GC0137_Draft_Final_Modification_Report.pdf](grid-code/S094-GC0137_Draft_Final_Modification_Report.pdf) |
| S095 | GC0137 minimum specification |  | 2022 | NG ESO | [S095-GC0137_Authority_Decision.pdf](grid-code/S095-GC0137_Authority_Decision.pdf) |
| S096 | GC0137 Ofgem decision |  |  | Ofgem | landing page: <https://www.ofgem.gov.uk/decision/grid-code-modification-gc0137-authority-decision> |
| S097 | GC0129 (Grid Code adopts G5/5) |  |  | NESO | [S097-GC0129_-_Final_Modification_Report_121119.pdf](grid-code/S097-GC0129_-_Final_Modification_Report_121119.pdf) |
| S098 | Review of voltage unbalance limit CC.6.1.5(b) |  |  | National Grid | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/37643-Voltage%20Unbalance%20Report.pdf> |
| S099 | Examples of generic Bilateral Connection Agreements |  |  | National Grid | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/Examples%20of%20Generic%20BCA's.pdf> |

## international

| id | title | version | date | publisher | file / status |
|---|---|---|---|---|---|
| S294 | NERC PRC-024-3 generator frequency/voltage protection |  |  | NERC | [S294-prc-024-3.pdf](international/S294-prc-024-3.pdf) |
| S295 | NERC PRC-006-3 technical rationale (UFLS) |  |  | NERC | [S295-prc-006-3.pdf](international/S295-prc-006-3.pdf) |
| S296 | PRC-006-NPCC-2 |  |  | NERC | [S296-prc-006-npcc-2.pdf](international/S296-prc-006-npcc-2.pdf) |
| S297 | PRC-006-SERC-3 |  | 2021 | NERC | [S297-regional_prc-006-serc-03_clean_08252021.pdf](international/S297-regional_prc-006-serc-03_clean_08252021.pdf) |
| S298 | NERC transmission system phase backup protection |  |  | NERC | [S298-transmission_system_phase_backup_protection._refdoc.pdf](international/S298-transmission_system_phase_backup_protection._refdoc.pdf) |
| S299 | ENTSO-E technical background for LFDD |  | 2014-12-15 | ENTSO-E | [S299-141215_Technical_background_for_LFDD.pdf](international/S299-141215_Technical_background_for_LFDD.pdf) |
| S300 | ENTSO-E NC ER final |  | 2015-03-25 | ENTSO-E | [S300-150325_ENTSO-E_NC_ER_final.pdf](international/S300-150325_ENTSO-E_NC_ER_final.pdf) |
| S301 | ENTSO-E NC ER supporting document |  | 2015-03-25 | ENTSO-E | [S301-150325_ENTSO-E_NC_ER_Supporting_Document_final.pdf](international/S301-150325_ENTSO-E_NC_ER_Supporting_Document_final.pdf) |
| S302 | Regulation (EU) 2017/2196 NC ER |  | 2017-11-24 | EU | [S302-CELEX_32017R2196_EN_TXT.pdf](international/S302-CELEX_32017R2196_EN_TXT.pdf) |
| S303 | Regulation (EU) 2016/631 RfG |  | 2016-04-14 | EU | [S303-CELEX_32016R0631_EN_TXT.pdf](international/S303-CELEX_32016R0631_EN_TXT.pdf) |
| S304 | ENTSO-E IGD frequency ranges |  | 2021-04-12 | ENTSO-E | [S304-210412_IGD_Frequency_ranges.pdf](international/S304-210412_IGD_Frequency_ranges.pdf) |
| S305 | RG-CE SAFA policy 5 emergency & restoration |  |  | ENTSO-E | [S305-5_-_Policy__on_Emergency_and_Restoration.pdf](international/S305-5_-_Policy__on_Emergency_and_Restoration.pdf) |
| S306 | Nordic SOA annex ER v2 | v2 | 2024 | ENTSO-E | [S306-Nordic_SOA_Annex_ER_v2_Approved.pdf](international/S306-Nordic_SOA_Annex_ER_v2_Approved.pdf) |
| S307 | IEEE PSRC report 084 overreaching distance relays |  |  | IEEE PSRC | [S307-084.pdf](international/S307-084.pdf) |
| S308 | IEEE PSRC D6 power swing and out-of-step |  | 2005 | IEEE PSRC | not fetched: host did not respond: <https://www.ewh.ieee.org/r6/san_francisco/pes/pes_pdf/OutOfStep/PowerSwingOOS.pdf> |
| S309 | IEEE PSRC report 079 distribution line protection practices |  |  | IEEE PSRC | [S309-079.pdf](international/S309-079.pdf) |
| S310 | IEEE PSRC report 109 IBR impact on protection |  |  | IEEE PSRC | [S310-109.pdf](international/S310-109.pdf) |
| S311 | IEEE PSRC report 095 |  |  | IEEE PSRC | [S311-095.pdf](international/S311-095.pdf) |
| S312 | Transformer protection application guide (Basler) |  |  | Basler/IEEE | [S312-transfguide.pdf](international/S312-transfguide.pdf) |
| S313 | Basler automatic reclosing |  |  | Basler | not fetched: dead link (HTTP 404): <https://www.basler.com/resource-hub/technical-paper/autorecl.pdf> |
| S314 | SEL distance element settings considerations |  |  | SEL | [S314-7005_SettingsConsiderations_BK_20210920_Web2.pdf](international/S314-7005_SettingsConsiderations_BK_20210920_Web2.pdf) |
| S315 | SEL five zones of distance protection |  |  | SEL | [S315-6864_ConsiderationsBenefits_BS_20180313_Web3.pdf](international/S315-6864_ConsiderationsBenefits_BS_20180313_Web3.pdf) |
| S316 | SEL ground fault protection guidelines |  |  | SEL | [S316-6065_ApplicationGuidelines_Web.pdf](international/S316-6065_ApplicationGuidelines_Web.pdf) |
| S317 | ABB DA handbook 8.2 relay coordination |  |  | ABB | [S317-DAHandbook_Section_08p02_Relay_Coordination_757285_ENa.pdf](international/S317-DAHandbook_Section_08p02_Relay_Coordination_757285_ENa.pdf) |
| S318 | Siemens 7SJ80 manual | V4.7 |  | Siemens | [S318-7SJ80xx_Manual_A8_V990800_us.pdf](international/S318-7SJ80xx_Manual_A8_V990800_us.pdf) |
| S319 | Siemens 7UT6x manual | V4.67 |  | Siemens | [S319-7UT6x_Manual_A5_V040400_en.pdf](international/S319-7UT6x_Manual_A5_V040400_en.pdf) |
| S320 | ElectraNet protection common functional requirements |  | 2024-12 | ElectraNet | [S320-1-09-FR-01-Protection-Common-Functional-Requirements.pdf](international/S320-1-09-FR-01-Protection-Common-Functional-Requirements.pdf) |
| S321 | AEMO access standard assessment guide |  | 2019-01 | AEMO | [S321-Access-Standard-Assessment-Guide-20190131.pdf](international/S321-Access-Standard-Assessment-Guide-20190131.pdf) |

## lom-rocof

| id | title | version | date | publisher | file / status |
|---|---|---|---|---|---|
| S107 | DC0079 retrospective application report to Authority |  | 2018 | DCRP | [S107-DCRP_MP_18_08_DC0079_Retrospective_Application_RTA_Final.pdf](lom-rocof/S107-DCRP_MP_18_08_DC0079_Retrospective_Application_RTA_Final.pdf) |
| S108 | DC0079 Ofgem decision |  | 2017-12-15 | Ofgem | [S108-dc0079_d.pdf](lom-rocof/S108-dc0079_d.pdf) |
| S109 | DC0079 Ofgem decision (DCode mirror) |  |  | DCode | [S109-DC0079_Ofgem_Decision.pdf](lom-rocof/S109-DC0079_Ofgem_Decision.pdf) |
| S110 | GC0079 industry consultation |  | 2017 | NGET | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/GC0079%20%20%20Industry%20Consultation%20Document.pdf> |
| S111 | GC0079 Annex 3 option 1 G59 proposals |  | 2017-07-31 | NGET | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/GC0079%20Annex%203%20Option%201%20G59%20%20proposals%20170731.pdf> |
| S112 | GC0079 modification page |  | 2016-19 | NESO | landing page: <https://www.neso.energy/industry-information/codes/gc/modifications/gc0079-frequency-changes-during-large-disturbances-and-their-effect-total-system-phase-1-2> |
| S113 | GC0035 industry consultation (frequency changes during large disturbances) |  | ~2013 | NGET | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/16943-Industry%20Consultation.pdf> |
| S114 | GC0035 (NESO copy) |  |  | NGET | [S114-33226-Volume_2A_-_Responses_to_First_Industry_Consultation.pdf](lom-rocof/S114-33226-Volume_2A_-_Responses_to_First_Industry_Consultation.pdf) |
| S115 | GC0035 (NESO copy 2) |  |  | NGET | [S115-16943-Industry_Consultation.pdf](lom-rocof/S115-16943-Industry_Consultation.pdf) |
| S116 | DCRP G59/3-1 modification report to Authority |  | 2015-06 | DCRP | [S116-1._DCRP_G59-3-1_Modification_-_Report_to_Authority_-_June_2015.pdf](lom-rocof/S116-1._DCRP_G59-3-1_Modification_-_Report_to_Authority_-_June_2015.pdf) |
| S117 | Northern Powergrid ALoMCP FAQs | v5.0 | ~2021 | NPg | [S117-5600.pdf](lom-rocof/S117-5600.pdf) |
| S118 | ALoMCP programme site |  | 2019-22 | ENA/ESO | landing page: <https://www.ena-eng.org/alomcp/> |
| S119 | ALoMCP knowledge base |  |  | ENA | landing page: <https://www.ena-eng.org/ALoMCP/mankb> |
| S120 | ALoMCP assessment flow chart |  |  | ENA | landing page: <https://www.energynetworks.org/publications/accelerated-loss-of-mains-change-programme-assessment-flow-chart> |
| S121 | UKPN loss of mains requirements |  |  | UKPN | landing page: <https://www.ukpowernetworks.co.uk/new-electricity-connections/distributed-energy-resources-der-generation/loss-of-mains-protection-requirements> |
| S122 | ENTSO-E IGD RoCoF withstand capability |  | 2018-01-31 | ENTSO-E | [S122-IGD_RoCoF_withstand_capability_final.pdf](lom-rocof/S122-IGD_RoCoF_withstand_capability_final.pdf) |
| S123 | ENTSO-E Inertia and RoCoF v17 | v17 | 2020 | ENTSO-E | [S123-Inertia_and_RoCoF_v17_clean.pdf](lom-rocof/S123-Inertia_and_RoCoF_v17_clean.pdf) |

## models

| id | title | version | date | publisher | file / status |
|---|---|---|---|---|---|
| S265 | GB 36-bus transmission model (PowerFactory) | ETYS 2013 basis |  | NESO | landing page: <https://www.neso.energy/publications/gb-36-bus-electricity-transmission-network-model> |
| S266 | Equivalent GB 36-zone system for frequency control studies (IJEPES 153) |  | 2023-11 | Elsevier (open access) | landing page: <https://www.sciencedirect.com/science/article/pii/S0142061523004477> |
| S267 | GB-36 bus open dataset paper |  | 2025 | arXiv | [S267-2508.08945v1.pdf](models/S267-2508.08945v1.pdf) |
| S268 | Edinburgh reduced GB (29 bus) and full GB (~2224 bus) |  | 2010 basis | Univ. Edinburgh | landing page: <https://www.maths.ed.ac.uk/OptEnergy/NetworkData/> |
| S269 | Imperial reduced GB dynamic test system (RGBN) |  | 2013 | Imperial College | [S269-KunjumuhammedEtAlTestSystemStabilityStudies_IEEEPowerTech2013_AuthorFinalVersion.pdf](models/S269-KunjumuhammedEtAlTestSystemStabilityStudies_IEEEPowerTech2013_AuthorFinalVersion.pdf) |
| S270 | Brunel GB transmission model thesis |  |  | Brunel | [S270-FullText.pdf](models/S270-FullText.pdf) |
| S271 | GB grid forming best practice guide |  | 2023-04 | NESO | [S271-GBGF_Best_Practice_Guide_April_2023.pdf](models/S271-GBGF_Best_Practice_Guide_April_2023.pdf) |
| S272 | Grid forming guidance note | Issue 4 |  | NESO | [S272-Grid_Forming_Guidance_Document_Issue_4.pdf](models/S272-Grid_Forming_Guidance_Document_Issue_4.pdf) |
| S273 | ENTSO-E grid forming capability of PPMs (phase II) |  | 2025-11-04 | ENTSO-E | [S273-20251104_GRID_FORMING_CAPABILITY_OF_POWER_PARK_MODULES.pdf](models/S273-20251104_GRID_FORMING_CAPABILITY_OF_POWER_PARK_MODULES.pdf) |
| S274 | ENTSO-E grid forming first interim report |  | 2024-05 | ENTSO-E | [S274-20240503_First_interim_report_in_technical_requirements.pdf](models/S274-20240503_First_interim_report_in_technical_requirements.pdf) |
| S275 | EPRI generic renewable model user guide | 3002027129 |  | EPRI | [S275-3002027129_Model_User_Guide_for_Generic_Renewable_Energy_Systems.pdf](models/S275-3002027129_Model_User_Guide_for_Generic_Renewable_Energy_Systems.pdf) |
| S276 | EPRI generic renewable model user guide (BPA mirror) | 3002006525 | 2015 | EPRI/BPA | [S276-Model_User_Guide_for_Generic_Renewable_Energy_System_Models.pdf](models/S276-Model_User_Guide_for_Generic_Renewable_Energy_System_Models.pdf) |
| S277 | WECC solar PV plant modelling guideline |  | 2024 | WECC | [S277-Solar_PV_Plant_Modeling_and_Validation_Guidline.pdf](models/S277-Solar_PV_Plant_Modeling_and_Validation_Guidline.pdf) |
| S278 | WECC IBR plant modelling guideline |  | 2026 | WECC | [S278-IBR_Power_Plant_Modeling_and_Validation_Guideline.pdf](models/S278-IBR_Power_Plant_Modeling_and_Validation_Guideline.pdf) |
| S279 | WECC REGFM_C1 grid-forming model spec |  | 2025-01-13 | WECC | [S279-Standard_Library_Grid-Forming_Hybrid_Control_Inverter-based_Resource_Model_Specification-REGFM_C1-20250113.pdf](models/S279-Standard_Library_Grid-Forming_Hybrid_Control_Inverter-based_Resource_Model_Specification-REGFM_C1-20250113.pdf) |
| S280 | REGFM_A1 (PNNL) |  | 2023 | OSTI | landing page: <https://www.osti.gov/biblio/2229442> |
| S281 | BPA approved dynamic models list |  | 2024-01 | BPA | [S281-Approved_Dynamic_Models_January_2024.pdf](models/S281-Approved_Dynamic_Models_January_2024.pdf) |
| S282 | NERC IBR performance guideline |  | 2018-09 | NERC | [S282-inverter-based_resource_performance_guideline.pdf](models/S282-inverter-based_resource_performance_guideline.pdf) |
| S283 | NERC DER_A parameterization guideline |  |  | NERC | not fetched: dead link (HTTP 404): <https://www.nerc.com/globalassets/our-work/guidelines/reliability/reliability-guideline_der_a_parameterization-clean.pdf> |
| S284 | NERC DER modelling parameters guideline |  | 2017-08-18 | NERC | [S284-reliability_guideline_-_der_modeling_parameters_-_2017-08-18_-_final.pdf](models/S284-reliability_guideline_-_der_modeling_parameters_-_2017-08-18_-_final.pdf) |
| S285 | IEEE 14-bus system (ICSEG) |  |  | ICSEG | landing page: <https://icseg.iti.illinois.edu/ieee-14-bus-system/> |
| S286 | WSCC 9-bus system (ICSEG) |  |  | ICSEG | landing page: <https://icseg.iti.illinois.edu/wscc-9-bus-system/> |
| S287 | Kundur two-area system |  |  | F. Gonzalez-Longatt | landing page: <https://www.fglongatt.org/Test_Systems/Test_2Areas_Kundur.html> |
| S288 | IEEE PES distribution test feeders |  |  | IEEE PES | landing page: <https://ewh.ieee.org/soc/pes/dsacom/testfeeders.html> |
| S289 | IEEE 123-node feeder zip |  |  | IEEE PES | [S289-feeder123.zip](models/S289-feeder123.zip) |
| S290 | IEEE 9500-node paper |  | 2022 | IEEE PES | [S290-9500-Node-PES-TPWRS-Paper-2022.01.14.pdf](models/S290-9500-Node-PES-TPWRS-Paper-2022.01.14.pdf) |
| S291 | CIGRE TB 575 benchmark systems (third-party copy) |  | 2014 | CIGRE | not fetched: host did not respond: <https://web.nit.ac.ir/~shahabi.m/M.Sc%20and%20PhD%20materials/DGs%20and%20MicroGrids%20Course/2014-Cigre_575_Benchmark%20Systems%20for%20Network%20Integration%20of%20Renewable%20and%20Distributed%20Energy%20Resources.pdf> |
| S292 | TAMU synthetic grid repository |  |  | Texas A&M | landing page: <https://electricgrids.engr.tamu.edu/> |
| S293 | IEEE 519-2022 review (ABB tech note 158) |  | 2022 | ABB | [S293-Tech_Note_158_IEEE_519-2022_Review.pdf](models/S293-Tech_Note_158_IEEE_519-2022_Review.pdf) |

## protection-dno

| id | title | version | date | publisher | file / status |
|---|---|---|---|---|---|
| S165 | ENWL EPD350 protection for 132/33/11/6.6 kV | Issue 6 |  | ENWL | [S165-epd350---protection-for-132kv-33kv-and-116.6kv-systems-i6.pdf](protection-dno/S165-epd350---protection-for-132kv-33kv-and-116.6kv-systems-i6.pdf) |
| S166 | ENWL EPD321 auto-reclosing of 33/11/6.6 kV OHL | Issue 4 | 2023-05 | ENWL | [S166-epd321-policy-for-automatic-reclosing-of-33116.6kv-overhead-lines-i4.pdf](protection-dno/S166-epd321-policy-for-automatic-reclosing-of-33116.6kv-overhead-lines-i4.pdf) |
| S167 | ENWL ES216 6.6-132 kV connections 9-240 MVA | Issue 4 |  | ENWL | [S167-es216-i4---6.6kv-to-132kv-connections-from-9mva-up-to-240mva.pdf](protection-dno/S167-es216-i4---6.6kv-to-132kv-connections-from-9mva-up-to-240mva.pdf) |
| S168 | ENWL CP335 earthing design for primary substations | Issue 4 |  | ENWL | [S168-cp335---earthing-design-for-gp-substations-i4.pdf](protection-dno/S168-cp335---earthing-design-for-gp-substations-i4.pdf) |
| S169 | NPg IMP/001/014 protection of distribution networks | v1.1 | 2016-07 | Northern Powergrid | [S169-2741.pdf](protection-dno/S169-2741.pdf) |
| S170 | NPg IMP/001/921 code of practice on fusing |  |  | Northern Powergrid | [S170-IMP001921_0.pdf](protection-dno/S170-IMP001921_0.pdf) |
| S171 | NPg IMP/001/010 standard customer connection arrangements |  |  | Northern Powergrid | [S171-4627.pdf](protection-dno/S171-4627.pdf) |
| S172 | SPEN ESDD-02-011 OHL protection and switchgear | Issue 9 |  | SPEN | not fetched: blocked (HTTP 403, needs a browser): <https://www.spenergynetworks.co.uk/userfiles/file/ESDD-02-011.pdf> |
| S173 | SPEN PROT-03-020 33 kV protection & control spec |  |  | SPEN | not fetched: blocked (HTTP 403, needs a browser): <https://www.spenergynetworks.co.uk/userfiles/file/PROT-03-020.pdf> |
| S174 | SPEN PROT-03-019 primary/secondary substation P&C |  |  | SPEN | not fetched: blocked (HTTP 403, needs a browser): <https://www.spenergynetworks.co.uk/userfiles/file/PROT-03-019.pdf> |
| S175 | SPEN ESDD-01-005 DG connection requirements |  |  | SPEN | not fetched: blocked (HTTP 403, needs a browser): <https://www.spenergynetworks.co.uk/userfiles/file/ESDD-01-005.pdf> |
| S176 | SPEN ESDD-01-008 export/import limiting schemes | Issue 4 |  | SPEN | not fetched: blocked (HTTP 403, needs a browser): <https://www.spenergynetworks.co.uk/userfiles/file/ESDD-01-008.pdf> |
| S177 | NGED ST:SD1E/6 export/import limiting schemes |  | 2022-10 | NGED | not fetched: blocked (HTTP 403, needs a browser): <https://commercial.nationalgrid.co.uk/downloads-view-reciteme/615347> |
| S178 | NGED POL:SD3/10 66 & 33 kV network design |  | 2023-10 | NGED | [S178-SD3_10.pdf](protection-dno/S178-SD3_10.pdf) |
| S179 | NGED POL:SD4/10 11 & 6.6 kV network design |  | 2023-10 | NGED | [S179-SD4_10.pdf](protection-dno/S179-SD4_10.pdf) |
| S180 | NGED ST:SD4A/6 design of 11/6.6 kV networks |  | 2023-03 | NGED | [S180-SD4A_6.pdf](protection-dno/S180-SD4A_6.pdf) |
| S181 | NGED ST:TP21AE earth fault clearance times |  | 2020-12 | NGED | not fetched: blocked (HTTP 403, needs a browser): <https://commercial.nationalgrid.co.uk/downloads-view-reciteme/607423> |
| S182 | NGED EE SPEC 98/13 approved relays |  |  | NGED | [S182-EE98_13.pdf](protection-dno/S182-EE98_13.pdf) |
| S183 | NGED protection relays spec |  |  | NGED | [S183-EE87_7.pdf](protection-dno/S183-EE87_7.pdf) |
| S184 | NGED ST:SD2A/0 |  | 2024-05 | NGED | [S184-SD2A_0.pdf](protection-dno/S184-SD2A_0.pdf) |
| S185 | UKPN EDS 05-4001 fuse ratings at distribution substations | v9.0 | 2022-08-11 | UKPN | not fetched: dead link (HTTP 404): <https://media.umbraco.io/uk-power-networks/cuxjplm5/eds-05-4001-fuse-ratings-at-distribution-substations.pdf> |
| S186 | UKPN EDS 08-5010 energy storage | v4.0 | 2022-12-05 | UKPN | not fetched: dead link (HTTP 404): <https://media.umbraco.io/uk-power-networks/r5vp5jyy/eds-08-5010-energy-storage.pdf> |
| S187 | SSEN G81 library: transmission |  |  | SSEN | landing page: <https://www.ssen.co.uk/our-services/tools-and-maps/g81/transmission/> |

## protection-tx

| id | title | version | date | publisher | file / status |
|---|---|---|---|---|---|
| S146 | Relevant Electrical Standards (RES) index | Issue 3 | 2018-03 | National Grid | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/RES_i3.pdf> |
| S147 | RES index (NESO copy) | Issue 3 |  | NESO | [S147-RES_i3.pdf](protection-tx/S147-RES_i3.pdf) |
| S148 | RES index | Issue 2 | 2014-10 | National Grid | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/39208-1.%20RES_i2.pdf> |
| S149 | TS 1 Ratings and general requirements for plant | Issue 1 | 2018-05 | National Grid | [S149-TS_1_RES_0.pdf](protection-tx/S149-TS_1_RES_0.pdf) |
| S150 | TS 1 draft | Issue 1 draft |  | National Grid | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/39206-2.%20RES_TS_1_i1-DRAFT.pdf> |
| S151 | TS 3.24.39 Circuit breaker fail protection |  |  | National Grid | [S151-39189-19._RES_TS_3.24.39_i1.pdf](protection-tx/S151-39189-19._RES_TS_3.24.39_i1.pdf) |
| S152 | TS 3.24.34 Busbar protection |  |  | National Grid | [S152-39190-18._RES_TS_3.24.34_i1.pdf](protection-tx/S152-39190-18._RES_TS_3.24.34_i1.pdf) |
| S153 | TS 3.02.04 Current transformers for protection |  |  | National Grid | [S153-39197-11._RES_TS_3.02.04_i1.pdf](protection-tx/S153-39197-11._RES_TS_3.02.04_i1.pdf) |
| S154 | Protection & control for HVDC systems TS |  |  | National Grid | [S154-39187-21._RES_TS_3.24.90_i1.pdf](protection-tx/S154-39187-21._RES_TS_3.24.90_i1.pdf) |
| S155 | TS 3.24.15 Environmental and test requirements for electronic equipment |  |  | National Grid | [S155-39191-17._RES_TS_3.24.15_i1.pdf](protection-tx/S155-39191-17._RES_TS_3.24.15_i1.pdf) |
| S156 | TS 3.24.95 Frequency response monitoring | Issue 2 |  | National Grid | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/TS_3.24.95_RES_i2.pdf> |
| S157 | TS 2.01 Part 1 Substations | Issue 1 |  | National Grid | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/TS_2.01_Part%201_RES_i1.pdf> |
| S158 | Back-up protection grading across NG/DNO interfaces |  |  | NG/NESO | [S158-PS_T_010_RES__Issue_1_002__0.pdf](protection-tx/S158-PS_T_010_RES__Issue_1_002__0.pdf) |
| S159 | GCRP 07/42 protection issues |  | 2007-11 | NGET | [S159-15634-pp07-42_-_Protection.pdf](protection-tx/S159-15634-pp07-42_-_Protection.pdf) |
| S160 | GC0023 consultation: fault clearance time & back-up protection | v1.0 | 2015-08-25 | National Grid | [S160-42526-Industry_Consultation.pdf](protection-tx/S160-42526-Industry_Consultation.pdf) |
| S161 | SHE Transmission electrical standards |  |  | SHET via NESO | [S161-34479-SHE_NGTS_2.6_-_Protection.pdf](protection-tx/S161-34479-SHE_NGTS_2.6_-_Protection.pdf) |
| S162 | SPT RES-01-100 relevant electrical standards |  | 2018-10-04 | SPEN via NESO | [S162-SPT_RES_Janaury_2017.pdf](protection-tx/S162-SPT_RES_Janaury_2017.pdf) |
| S163 | Electrical standards documents hub |  |  | NESO | landing page: <https://www.neso.energy/industry-information/codes/grid-code-gc/electrical-standards-documents> |
| S164 | SPEN ESDD-02-006 calculation of system fault levels | Issue 4 |  | SPEN | not fetched: blocked (HTTP 403, needs a browser): <https://www.spenergynetworks.co.uk/userfiles/file/ESDD-02-006.pdf> |

## sqss

| id | title | version | date | publisher | file / status |
|---|---|---|---|---|---|
| S001 | Planning & operating standards under BETTA vol 2 (draft GB SQSS v0.6) | v0.6 draft | 2004-07 | Ofgem/DTI | [S001-7661-15604_vol2_0.pdf](sqss/S001-7661-15604_vol2_0.pdf) |
| S002 | Planning & operating standards under BETTA vol 1 (conclusions) |  | 2004-09 | Ofgem/DTI | [S002-8445-22404_vol1.pdf](sqss/S002-8445-22404_vol1.pdf) |
| S003 | GB SQSS v1.0 (Annex 8) | v1.0 | 2004 (pub 2008) | Ofgem | [S003-annex-8-gbsqss_0.pdf](sqss/S003-annex-8-gbsqss_0.pdf) |
| S004 | GCRP 03/11 paper on NGC SQSS (pre-BETTA) |  | 2003-05-22 | NGC | [S004-15022-Generic_Provisions_Working_Group_Final_Report.pdf](sqss/S004-15022-Generic_Provisions_Working_Group_Final_Report.pdf) |
| S005 | GCRP paper Nov 2002 (pre-BETTA Grid Code/SQSS context) |  | 2002-11 | NGC | [S005-14904-Embedded_Power_Stations_Working_Group.pdf](sqss/S005-14904-Embedded_Power_Stations_Working_Group.pdf) |
| S006 | SQSS consultation and new version of standard (GSR008 fundamental review) | v2.x draft | ~2011 | National Grid | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/12742-SQSS%20consultation%20and%20new%20version%20of%20standard.pdf> |
| S007 | GB SQSS Fundamental Review outline principles |  | ~2008 | NGET | [S007-12734-Outline_Principles_Document.pdf](sqss/S007-12734-Outline_Principles_Document.pdf) |
| S008 | GB SQSS Fundamental Review IB WG1 terms of reference |  | 2008-09 | NGET | [S008-12744-Working_Group_1_International_Benchmarking_-_Terms_of_Reference.pdf](sqss/S008-12744-Working_Group_1_International_Benchmarking_-_Terms_of_Reference.pdf) |
| S009 | GSR007 Ofgem decision letter (infeed loss limits) | v2.0->v2.1 | 2011-01 | Ofgem | [S009-gsr007-decision-letter-final.pdf](sqss/S009-gsr007-decision-letter-final.pdf) |
| S010 | GSR007 consultation: Review of Infeed Loss Limits |  | 2010 | NGET | [S010-26032-Consultation_Document.pdf](sqss/S010-26032-Consultation_Document.pdf) |
| S011 | GSR007 impact assessment | final | 2010-10 | Ofgem | [S011-gsr007-ia-final_0.pdf](sqss/S011-gsr007-ia-final_0.pdf) |
| S012 | GSR002 housekeeping amendment report |  | ~2009 | NGET | [S012-26022-Amendment_Report.pdf](sqss/S012-26022-Amendment_Report.pdf) |
| S013 | GSR013 Offshore Infeed Loss WG report |  | ~2013 | NGET | [S013-12564-Workgroup_Report_Consultation_Document.pdf](sqss/S013-12564-Workgroup_Report_Consultation_Document.pdf) |
| S014 | GSR011 Offshore WG report |  | 2013-05 | NGET | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/34873-GSR011%20Working-Group%20Report.pdf> |
| S015 | GSR011 Ofgem decision letter |  | 2016-07 | Ofgem | [S015-authority_decision_letter_gsr011-1.pdf](sqss/S015-authority_decision_letter_gsr011-1.pdf) |
| S016 | GSR015 Normal Infeed Loss Risk report |  | 2014-03-10 | NGET | [S016-28604-Industry_Consultation.pdf](sqss/S016-28604-Industry_Consultation.pdf) |
| S017 | GSR015 report (alternate ID) |  | 2014 | NGET | [S017-31982-Modification_Report.pdf](sqss/S017-31982-Modification_Report.pdf) |
| S018 | GSR015 Ofgem decision |  | 2014-12-03 | Ofgem | [S018-141203_gsr015_decision.pdf](sqss/S018-141203_gsr015_decision.pdf) |
| S019 | GSR017 update |  | ~2015 | NGET | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/GSR017%20Update.pdf> |
| S020 | NETS SQSS v2.3 | v2.3 | 2017-02 | NGET | not fetched: dead link (HTTP 404): <https://www.nationalgrid.com/sites/default/files/documents/NETS%20SQSS%20V2.3.pdf> |
| S021 | Statutory consultation: SQSS version number change |  | 2018-02 | Ofgem | [S021-statutory_consultation_notice_sqss_version_number_change.pdf](sqss/S021-statutory_consultation_notice_sqss_version_number_change.pdf) |
| S022 | GSR024 decision document |  | ~2020 | Ofgem (via NESO) | [S022-SQSS_GSR027_Authority_Decision.pdf](sqss/S022-SQSS_GSR027_Authority_Decision.pdf) |
| S023 | GSR025 Ofgem decision (ER P28 voltage fluctuation) |  | 2020-12-10 | Ofgem | [S023-sqss_gsr025_authority_decision__0.pdf](sqss/S023-sqss_gsr025_authority_decision__0.pdf) |
| S024 | GSR026 Ofgem decision (non-standard voltages) |  | 2020-12-10 | Ofgem | [S024-sqss_gsr026_authority_decision.pdf](sqss/S024-sqss_gsr026_authority_decision.pdf) |
| S025 | GSR027 Ofgem decision (frequency control criteria; creates FRCR) |  | 2020-12 | Ofgem | [S025-sqss_gsr027_authority_decision_0.pdf](sqss/S025-sqss_gsr027_authority_decision_0.pdf) |
| S026 | GB SQSS full text (likely v2.5) | v2.5? | 2021-04 | NG ESO | [S026-NETS_SQSS_V2.5_DRAFT_for_Authority_Licence_Consultation.pdf](sqss/S026-NETS_SQSS_V2.5_DRAFT_for_Authority_Licence_Consultation.pdf) |
| S027 | GB SQSS full text (version unknown) | v2.5-2.6? | ~2021 | NG ESO | [S027-NETS_SQSS_V2.5_.pdf](sqss/S027-NETS_SQSS_V2.5_.pdf) |
| S028 | GB SQSS full text (older, version unknown) | v2.2-2.4? |  | NGET/ESO | [S028-8589935019-NETS_SQSS_V2_2_-_GSR008-1.pdf](sqss/S028-8589935019-NETS_SQSS_V2_2_-_GSR008-1.pdf) |
| S029 | GB SQSS full text (version unknown) |  |  | NGET/ESO | [S029-8589934858-NETS_SQSS_V2_X_-_ALL_GSR008-1_Changes_31_03_2016.pdf](sqss/S029-8589934858-NETS_SQSS_V2_X_-_ALL_GSR008-1_Changes_31_03_2016.pdf) |
| S030 | NETS SQSS (version unknown) A |  |  | NGET/ESO | [S030-8589938837-GSR017_Update.pdf](sqss/S030-8589938837-GSR017_Update.pdf) |
| S031 | NETS SQSS (version unknown) B |  |  | NGET/ESO | [S031-8589935376-Modification_Register_January_2016.pdf](sqss/S031-8589935376-Modification_Register_January_2016.pdf) |
| S032 | NETS SQSS (version unknown) C |  |  | NGET/ESO | [S032-DRAFT_NETS_SQSS_V2.6_RB_.pdf](sqss/S032-DRAFT_NETS_SQSS_V2.6_RB_.pdf) |
| S033 | NETS SQSS (version unknown) D |  |  | NGET/ESO | [S033-NETS_SQSS_24_June_2009_v2.pdf](sqss/S033-NETS_SQSS_24_June_2009_v2.pdf) |
| S034 | NETS SQSS (version unknown) E |  |  | NGET/ESO | [S034-NETS_Security_and_Quality_of_Supply_Standards_v2.9.pdf](sqss/S034-NETS_Security_and_Quality_of_Supply_Standards_v2.9.pdf) |
| S035 | NETS SQSS (version unknown) F |  |  | NGET/ESO | [S035-SQSS_Modification_Register_Feb_2020.pdf](sqss/S035-SQSS_Modification_Register_Feb_2020.pdf) |
| S036 | NETS SQSS current listing | v2.10-2.12? | 2025-2026 | NESO | [S036-NETS_Security_and_Quality_of_Supply_Standards_v2.10_0.pdf](sqss/S036-NETS_Security_and_Quality_of_Supply_Standards_v2.10_0.pdf) |
| S037 | GSR034 Final Modification Report (offshore DC converter loss) |  | 2025/26 | NESO | [S037-GSR034_Final_Modification_Report_0.pdf](sqss/S037-GSR034_Final_Modification_Report_0.pdf) |
| S038 | GSR034 Ofgem decision |  | 2026-03 | Ofgem | [S038-GSR034-Review-of-Loss-of-Power-Infeed-Risk-for-Offshore-DC-Converters.pdf](sqss/S038-GSR034-Review-of-Loss-of-Power-Infeed-Risk-for-Offshore-DC-Converters.pdf) |
| S039 | SQSS code documents (all versions v2.5-current) |  |  | NESO | landing page: <https://www.neso.energy/industry-information/codes/security-and-quality-supply-standard-sqss/sqss-code-documents> |
| S040 | GSR027 modification page |  | 2020 | NG ESO | landing page: <https://www.nationalgrideso.com/industry-information/codes/security-and-quality-supply-standards-old/modifications/gsr027-review> |

## voll

| id | title | version | date | publisher | file / status |
|---|---|---|---|---|---|
| S322 | Value of Lost Load for electricity in GB (London Economics) |  | 2013-07 | Ofgem/DECC | [S322-london-economics-value-of-lost-load-for-electricity-in-gb_0.pdf](voll/S322-london-economics-value-of-lost-load-for-electricity-in-gb_0.pdf) |
| S323 | DECC EMR annex C reliability standard methodology |  | 2013-07 | DECC | [S323-emr_consultation_annex_c.pdf](voll/S323-emr_consultation_annex_c.pdf) |
| S324 | Ofgem BSC P305 decision |  | 2015-04-02 | Ofgem | [S324-p305d_1.pdf](voll/S324-p305d_1.pdf) |

## carbon

| id | title | version | date | publisher | file / status |
|---|---|---|---|---|---|
| S325 | Carbon Intensity Regional Forecast Methodology (Table 1: per-fuel gCO2/kWh used by CarbonFactors) | v2 | 2024-05 | NESO (with EDF Europe, WWF, University of Oxford) | [S325-neso-ci-regional-methodology-v2.pdf](carbon/S325-neso-ci-regional-methodology-v2.pdf) |
