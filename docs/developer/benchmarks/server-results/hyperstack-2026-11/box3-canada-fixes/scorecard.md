## Candidate (RC3+P1/P2), box 3 vs other solvers from box 2 (same CPU class, 2026-11-17)

{'win': 62, 'nobody': 5}

## RC3 baseline, box 3 vs other solvers from box 2 (same CPU class, 2026-11-17)

{'win': 53, 'loss': 7, 'nobody': 5, 'tie': 2}


### Candidate per case (ms)

              case  gridsim_ms  fastest_other_ms result
        case10ba.m       0.079             0.406    win
         case118.m       1.086             6.478    win
       case118zh.m       0.871             5.155    win
        case1197.m       4.908            27.947    win
        case12da.m       0.081             0.360    win
  case1354pegase.m      16.180            45.712    win
       case136ma.m       1.179             6.344    win
          case14.m       0.118             0.591    win
         case141.m       0.721             5.002    win
         case145.m       2.905            10.686    win
        case15da.m       0.108             0.449    win
       case15nbr.m       0.114             0.436    win
        case16am.m         NaN               NaN nobody
        case16ci.m       0.096             0.445    win
        case17me.m       0.141             0.539    win
          case18.m       0.173             0.576    win
     case1888rte.m         NaN               NaN nobody
       case18nbr.m       0.116             0.530    win
     case1951rte.m         NaN               NaN nobody
          case22.m       0.133             0.570    win
      case2383wp.m      27.496            95.237    win
 case24_ieee_rts.m       0.186             1.107    win
      case2736sp.m      37.680               NaN    win
     case2737sop.m      40.394           239.024    win
     case2746wop.m      42.779               NaN    win
      case2746wp.m      42.251           222.815    win
     case2848rte.m      67.728           308.303    win
     case2868rte.m         NaN               NaN nobody
  case2869pegase.m      45.111            81.266    win
        case28da.m       0.185             0.707    win
          case30.m       0.204             1.055    win
         case300.m       2.508            16.750    win
      case3012wp.m      70.174           144.018    win
         case30Q.m       0.219             1.126    win
       case30pwl.m       0.235             1.049    win
      case3120sp.m      47.045           145.788    win
      case3375wp.m      77.841           197.785    win
        case33bw.m       0.205             0.806    win
        case33mg.m       0.261             0.821    win
        case34sa.m       0.237             0.848    win
        case38si.m       0.303             0.887    win
          case39.m       0.341             1.332    win
      case4_dist.m       0.023             0.166    win
         case4gs.m       0.034             0.226    win
           case5.m       0.032             0.271    win
        case51ga.m       0.407             1.338    win
        case51he.m       0.349             1.255    win
    case533mt_hi.m       2.576            12.847    win
    case533mt_lo.m       2.787            13.638    win
          case57.m       0.516             4.049    win
          case59.m       0.892             2.655    win
    case60nordic.m       0.568             4.582    win
          case69.m       0.669             1.839    win
         case6ww.m       0.045             0.299    win
        case70da.m       0.649             1.720    win
        case74ds.m       0.584             3.606    win
          case85.m       0.622             3.918    win
    case89pegase.m       1.295             7.343    win
           case9.m       0.076             0.352    win
        case94pi.m       0.547             4.314    win
          case9Q.m       0.079             0.370    win
     case9target.m       0.104             0.438    win
 case_ACTIVSg200.m       1.515             8.707    win
case_ACTIVSg2000.m      45.766            61.986    win
 case_ACTIVSg500.m       3.624            14.042    win
   case_RTS_GMLC.m         NaN               NaN nobody
     case_ieee30.m       0.259             1.175    win

## Focused 16, cand (full-suite run) vs other solvers measured on box 3

              case  gridsim_ms  fastest_other_ms result
         case118.m        1.09              6.02    win
        case1197.m        4.91             28.08    win
  case1354pegase.m       16.18             49.18    win
      case2383wp.m       27.50            101.79    win
      case2736sp.m       37.68               NaN    win
     case2737sop.m       40.39            234.12    win
     case2746wop.m       42.78               NaN    win
      case2746wp.m       42.25            233.26    win
     case2848rte.m       67.73            328.59    win
  case2869pegase.m       45.11             81.60    win
         case300.m        2.51             15.53    win
      case3012wp.m       70.17            140.30    win
      case3120sp.m       47.05            129.75    win
      case3375wp.m       77.84            192.07    win
case_ACTIVSg2000.m       45.77             69.55    win
 case_ACTIVSg500.m        3.62             14.84    win

{'win': 16}

## Focused 16, base (full-suite run) vs other solvers measured on box 3

              case  gridsim_ms  fastest_other_ms result
         case118.m        1.07              6.02    win
        case1197.m        5.91             28.08    win
  case1354pegase.m       22.30             49.18    win
      case2383wp.m      113.15            101.79   loss
      case2736sp.m      161.00               NaN    win
     case2737sop.m      220.43            234.12    tie
     case2746wop.m      267.90               NaN    win
      case2746wp.m      272.09            233.26   loss
     case2848rte.m         NaN            328.59   loss
  case2869pegase.m       75.06             81.60    tie
         case300.m        3.29             15.53    win
      case3012wp.m      491.67            140.30   loss
      case3120sp.m      227.77            129.75   loss
      case3375wp.m      580.50            192.07   loss
case_ACTIVSg2000.m      305.04             69.55   loss
 case_ACTIVSg500.m        5.62             14.84    win

{'win': 7, 'loss': 7, 'tie': 2}

