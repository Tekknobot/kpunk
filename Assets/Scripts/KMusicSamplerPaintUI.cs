using UnityEngine;
using UnityEngine.UIElements;
using KMusic.UI;

public class KMusicSamplerPaintUI : MonoBehaviour
{
    [SerializeField] private UIDocument doc;

    private KMusicDrumSequencer sequencer;

    private StepGrid sampleGrid;
    private StepGrid drumGrid;

    private Button[] chopBtns = new Button[16];
    private Button[] drumBtns = new Button[8];

    private int activeChop = 1; // 1..16
    private int activeDrum = 0; // 0..7

    // Sample pattern (stores chop id per step)
    private int[,] samplePattern = new int[2, 8];

    // Drum lanes
    private bool[][,] drumPatterns = new bool[8][,];

    private static readonly Color[] FALLBACK_DRUM_COLORS = new Color[8]
    {
        new Color(0.90f,0.55f,0.20f,1f),
        new Color(0.95f,0.30f,0.35f,1f),
        new Color(0.85f,0.35f,0.90f,1f),
        new Color(0.35f,0.85f,0.95f,1f),
        new Color(0.25f,0.95f,0.55f,1f),
        new Color(0.95f,0.90f,0.25f,1f),
        new Color(0.55f,0.70f,0.95f,1f),
        new Color(0.95f,0.60f,0.80f,1f),
    };

    private void Awake()
    {
        if (!doc) doc = GetComponent<UIDocument>();
    }

    private void OnEnable()
    {
        if (!doc) return;

        var root = doc.rootVisualElement;

        sequencer = FindObjectOfType<KMusicDrumSequencer>();

        sampleGrid = root.Q<StepGrid>("SampleStepGrid");
        drumGrid   = root.Q<StepGrid>("DrumStepGrid");

        if (sampleGrid == null) Debug.LogError("SampleStepGrid not found");
        if (drumGrid == null)   Debug.LogError("DrumStepGrid not found");

        // enable labels
        drumGrid?.EnableValueLabels(true, v => v == 0 ? "" : "•");

        // Chop buttons
        for (int i = 0; i < 16; i++)
            chopBtns[i] = root.Q<Button>($"Chop{(i+1):00}");

        // Drum buttons
        drumBtns[0] = root.Q<Button>("DrumKick");
        drumBtns[1] = root.Q<Button>("DrumSnare");
        drumBtns[2] = root.Q<Button>("DrumClap");
        drumBtns[3] = root.Q<Button>("DrumHatC");
        drumBtns[4] = root.Q<Button>("DrumHatO");
        drumBtns[5] = root.Q<Button>("DrumRim");
        drumBtns[6] = root.Q<Button>("DrumPerc");
        drumBtns[7] = root.Q<Button>("DrumCrash");

        // init drum memory
        for (int d = 0; d < 8; d++)
            drumPatterns[d] = new bool[2,8];

        WireChops();
        WireDrums();
        WireGrids();

        // sample labels show chop numbers
        sampleGrid?.EnableValueLabels(true, v => (v>=1 && v<=16)? v.ToString("00"):"");

        sampleGrid?.ClearAll();
        drumGrid?.ClearAll();

        sampleGrid?.EnableToggleEraseOnSameValue(true);
        drumGrid?.EnableToggleEraseOnSameValue(true);

        SelectChop(1);
        SelectDrum(0);
        LoadDrumLaneToGrid(0);
    }

    // ---------- CHOPS ----------

    private void WireChops()
    {
        for (int i=0;i<16;i++)
        {
            int id=i+1;
            var btn=chopBtns[i];
            if(btn==null) continue;
            btn.clicked += ()=> SelectChop(id);
        }
    }

    private void SelectChop(int id)
    {
        activeChop=Mathf.Clamp(id,1,16);
        sampleGrid?.SetPaintValue(activeChop);

        for(int i=0;i<16;i++)
        {
            var b=chopBtns[i];
            if(b==null) continue;
            if(i+1==activeChop) b.AddToClassList("km-chop-btn--active");
            else b.RemoveFromClassList("km-chop-btn--active");
        }
    }

    // ---------- DRUMS ----------

    private void WireDrums()
    {
        for(int i=0;i<8;i++)
        {
            int lane=i;
            var btn=drumBtns[i];
            if(btn==null) continue;
            btn.clicked += ()=> SelectDrum(lane);
        }
    }

    private void SelectDrum(int lane)
    {
        lane=Mathf.Clamp(lane,0,7);

        SaveGridToDrumLane(activeDrum);
        activeDrum=lane;
        LoadDrumLaneToGrid(activeDrum);

        if(drumGrid!=null)
        {
            drumGrid.SetActiveTint(GetActiveDrumColor(activeDrum));
            drumGrid.RefreshAll();
            drumGrid.SetPaintValue(lane+1);
            drumGrid.EnableValueLabels(true, v => v switch {
                1=>"K",2=>"S",3=>"C",4=>"HC",5=>"HO",6=>"RD",7=>"RM",8=>"X",_=>""
            });
        }

        for(int i=0;i<8;i++)
        {
            var b=drumBtns[i];
            if(b==null) continue;
            if(i==activeDrum) b.AddToClassList("km-drum-btn--active");
            else b.RemoveFromClassList("km-drum-btn--active");
        }
    }

    private void SaveGridToDrumLane(int lane)
    {
        if (drumGrid == null) return;

        for (int r = 0; r < drumGrid.RowCount; r++)
        for (int c = 0; c < drumGrid.ColCount; c++)
            drumPatterns[lane][r, c] = (drumGrid.GetValue(r, c) != 0);
    }
    
    private void LoadDrumLaneToGrid(int lane)
    {
        if (drumGrid == null) return;

        drumGrid.ClearAll();

        // bool pattern -> show the CURRENT lane id as the cell value (lane+1)
        for (int r = 0; r < drumGrid.RowCount; r++)
        for (int c = 0; c < drumGrid.ColCount; c++)
        {
            int v = drumPatterns[lane][r, c] ? (lane + 1) : 0;
            drumGrid.SetValue(r, c, v, fireEvent: false);
        }
    }
    private Color GetActiveDrumColor(int lane)
    {
        var btn=drumBtns[lane];
        if(btn!=null)
        {
            var c=btn.resolvedStyle.borderTopColor;
            if(c.a>0.01f) return c;
        }
        return FALLBACK_DRUM_COLORS[lane];
    }

    // ---------- GRID EVENTS ----------

    private void WireGrids()
    {
        if(sampleGrid!=null)
        {
            sampleGrid.OnCellValueChanged += (r,c,v)=> samplePattern[r,c]=v;

            sampleGrid.OnCellClicked += (r,c)=>{
                int v=sampleGrid.GetValue(r,c);
                if(v>=1 && v<=16) SelectChop(v);
            };
        }

        if(drumGrid!=null)
        {
            drumGrid.OnCellValueChanged += (r,c,v)=>
            {
                bool on = v != 0;
                drumPatterns[activeDrum][r,c] = on;

                if (sequencer == null) return;

                int step = r * 8 + c;
                int lane = activeDrum;

                if (on)
                    sequencer.SetStepLane(step, lane, true);
                else
                    sequencer.SetStepLane(step, lane, false);
            };
        }
    }
}